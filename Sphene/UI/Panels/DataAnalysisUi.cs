using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.FileCache;
using Sphene.Interop.Ipc;
using Sphene.SpheneConfiguration;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.Utils;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Runtime.InteropServices;
using Sphene.UI.Theme;
using Sphene.Services.ModLearning.Models;
using Sphene.Services.CharaData;
using Sphene.PlayerData.Data;

namespace Sphene.UI.Panels;

public class DataAnalysisUi : WindowMediatorSubscriberBase
{
    private readonly record struct CurrentReplacement(string Hash, string FileSwapPath, string[] GamePaths);
    private readonly record struct ExpectedReplacement(ObjectKind Kind, FileReplacement Replacement);
    private readonly CharacterAnalyzer _characterAnalyzer;
    private readonly Progress<(string, int)> _conversionProgress = new();
    private readonly IpcManager _ipcManager;
    private readonly UiSharedService _uiSharedService;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly PlayerPerformanceConfigService _playerPerformanceConfig;
    private readonly TransientResourceManager _transientResourceManager;
    private readonly TransientConfigService _transientConfigService;
    private readonly TextureBackupService _textureBackupService;
    private readonly ShrinkU.Services.TextureBackupService _shrinkuBackupService;
    private readonly ShrinkU.Services.TextureConversionService _shrinkuConversionService;
    private readonly CharacterDataSqliteStore _characterDataSqliteStore;
    private readonly SpheneConfigService _spheneConfigService;
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
    private readonly Lock _modLearningLock = new();
    private List<string> _modLearningCharacters = [];
    private Dictionary<string, List<LearnedModState>> _modLearningStatesByMod = new(StringComparer.Ordinal);
    private string _selectedModLearningCharacter = string.Empty;
    private string _selectedModLearningMod = string.Empty;
    private string _selectedModLearningOption = string.Empty;
    private uint _selectedModLearningJobId = 0;
    private string _modLearningFilter = string.Empty;
    private bool _showModLearningJson = true;
    private Dictionary<string, string> _modLearningJsonByMod = new(StringComparer.Ordinal);
    private HashSet<string> _modLearningCurrentReplacementKeys = new(StringComparer.OrdinalIgnoreCase);
    private string _modLearningCurrentDataHash = string.Empty;
    private List<CurrentReplacement> _modLearningCurrentReplacements = [];
    private Task? _modLearningLoadTask;
    private bool _modLearningLoading = false;
    private bool _modLearningAutoSelected = false;
    private bool _modLearningTabActive = false;
    private bool _modLearningFirstTabVisitDone = false;
    private bool _modLearningRefreshRequested = false;
    private static readonly JsonSerializerOptions ModLearningJsonOptions = new()
    {
        WriteIndented = true
    };

    public DataAnalysisUi(ILogger<DataAnalysisUi> logger, SpheneMediator mediator,
        CharacterAnalyzer characterAnalyzer, IpcManager ipcManager,
        PerformanceCollectorService performanceCollectorService, UiSharedService uiSharedService,
        DalamudUtilService dalamudUtilService, PlayerPerformanceConfigService playerPerformanceConfig, TransientResourceManager transientResourceManager,
        TransientConfigService transientConfigService, TextureBackupService textureBackupService,
        ShrinkU.Services.TextureBackupService shrinkuBackupService,
        ShrinkU.Services.TextureConversionService shrinkuConversionService,
        CharacterDataSqliteStore characterDataSqliteStore,
        SpheneConfigService spheneConfigService)
        : base(logger, mediator, "Sphene Character Data Analysis", performanceCollectorService)
    {
        _characterAnalyzer = characterAnalyzer;
        _ipcManager = ipcManager;
        _uiSharedService = uiSharedService;
        _dalamudUtilService = dalamudUtilService;
        _playerPerformanceConfig = playerPerformanceConfig;
        _transientResourceManager = transientResourceManager;
        _transientConfigService = transientConfigService;
        _textureBackupService = textureBackupService;
        _shrinkuBackupService = shrinkuBackupService;
        _shrinkuConversionService = shrinkuConversionService;
        _characterDataSqliteStore = characterDataSqliteStore;
        _spheneConfigService = spheneConfigService;
        Mediator.Subscribe<CharacterDataAnalyzedMessage>(this, (_) =>
        {
            _hasUpdate = true;
        });
        Mediator.Subscribe<CharacterDataCreatedMessage>(this, (_) =>
        {
            if (_modLearningTabActive)
            {
                _modLearningRefreshRequested = true;
            }
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
            if (ImGui.BeginPopupModal("BC7 Conversion in Progress"))
            {
                using (SpheneCustomTheme.ApplyContextMenuTheme())
                {
                ImGui.TextColored(SpheneCustomTheme.Colors.SpheneGold, "Converting Mods");
                ImGui.Separator();
                var modsPercent = _totalMods > 0 ? (float)Math.Clamp(_currentModIndex / (double)_totalMods, 0.0, 1.0) : 0f;
                ImGui.Text($"Batch progress: {_currentModIndex} / {_totalMods} mods");
                ImGui.ProgressBar(modsPercent, new Vector2(-1, 0), $"{modsPercent * 100:F1}%");
                if (_currentModStartedAt != DateTime.MinValue && _currentModIndex >= 0)
                {
                    var elapsedMods = DateTime.UtcNow - _currentModStartedAt;
                    ImGui.Text($"Current mod elapsed: {elapsedMods:mm\\:ss}");
                }
                ImGui.Spacing();
                if (!string.IsNullOrEmpty(_currentModName))
                    ImGui.Text($"Current mod: {_currentModName}");
                var totalFiles = _currentModTotalFiles > 0 ? _currentModTotalFiles : _texturesToConvert.Count;
                var currentFile = _conversionCurrentFileProgress;
                var progressPercentage = totalFiles > 0 ? (float)currentFile / totalFiles : 0f;
                ImGui.Text($"Current mod progress: {currentFile} / {totalFiles} files");
                ImGui.ProgressBar(progressPercentage, new Vector2(-1, 0), $"{progressPercentage * 100:F1}%");
                if (!string.IsNullOrEmpty(_conversionCurrentFileName))
                    UiSharedService.TextWrapped("Current file: " + _conversionCurrentFileName);
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.StopCircle, "Cancel conversion"))
                {
                    try { _shrinkuConversionService.Cancel(); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Failed to cancel ShrinkU conversion"); }
                }
                UiSharedService.SetScaledWindowSize(500);
                }
                ImGui.EndPopup();
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
        using (var tabItem = ImRaii.TabItem("Mod Learning"))
        {
            if (tabItem)
            {
                _modLearningTabActive = true;
                if (!_modLearningFirstTabVisitDone)
                {
                    _modLearningFirstTabVisitDone = true;
                    _modLearningRefreshRequested = true;
                }
                DrawModLearning();
            }
            else
            {
                _modLearningTabActive = false;
            }
        }
    }

    private bool _showAlreadyAddedTransients = false;
    private bool _acknowledgeReview = false;
    private string _selectedStoredCharacter = string.Empty;
    private string _selectedJobEntry = string.Empty;
    private readonly List<string> _storedPathsToRemove = [];
    private readonly Dictionary<string, string> _filePathResolve = [];
    private string _filterGamePath = string.Empty;
    private string _filterFilePath = string.Empty;

    private void DrawModLearning()
    {
        if (_modLearningRefreshRequested)
        {
            _modLearningRefreshRequested = false;
            LoadModLearningStates(forceReload: true);
        }

        EnsureModLearningLoaded();

        if (_modLearningLoading)
        {
            ImGui.TextUnformatted("Loading...");
            return;
        }

        EnsureModLearningCurrentCharacterSelected();

        using var container = ImRaii.Child("##modlearning_container", new Vector2(0, 0), false, ImGuiWindowFlags.NoScrollbar);
        if (!container) return;

        var listHeight = 150f * ImGuiHelpers.GlobalScale;
        var selectorHeight = 205f * ImGuiHelpers.GlobalScale;
        var modDisplayNames = GetModDisplayNamesSnapshot();
        var jobKeys = GetJobsForSelectedCharacter();
        if (!jobKeys.Contains(_selectedModLearningJobId))
        {
            _selectedModLearningJobId = jobKeys[0];
        }
        var modsForJob = GetModsForSelectedJob();
        var sortedModsForJob = modsForJob
            .OrderBy(mod => GetModDisplayName(modDisplayNames, mod), StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (modsForJob.Count > 0 && !modsForJob.Contains(_selectedModLearningMod, StringComparer.Ordinal))
        {
            _selectedModLearningMod = modsForJob[0];
            _selectedModLearningOption = string.Empty;
        }
        var options = GetOptionsForSelectedMod();
        if (options.Count > 0 && !options.ContainsKey(_selectedModLearningOption))
        {
            _selectedModLearningOption = options.Keys.FirstOrDefault() ?? string.Empty;
        }
        var runtime = GetCurrentModRuntimeStatus(_selectedModLearningMod);
        var currentKeysSnapshot = GetCurrentReplacementKeysSnapshot();
        var emoteOverriddenMods = GetEmoteOverriddenModsForJob(sortedModsForJob, _selectedModLearningJobId, currentKeysSnapshot);
        var currentJobId = _dalamudUtilService.ClassJobId;
        var currentJobLabel = _uiSharedService.JobData.TryGetValue((ushort)currentJobId, out var currentJobName) ? currentJobName : currentJobId.ToString();

        ImGui.SetNextItemWidth(240f * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##modlearning_filter", "Filter mods", ref _modLearningFilter, 255);
        ImGui.SameLine();
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.SyncAlt, "Refresh"))
        {
            LoadModLearningStates(forceReload: true);
        }
        ImGui.SameLine();
        ImGui.TextUnformatted($"Selected: {_selectedModLearningCharacter} / Job {_selectedModLearningJobId} / {GetModDisplayName(modDisplayNames, _selectedModLearningMod)}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"Current Job: {currentJobLabel} ({currentJobId})");

        using (ImRaii.Child("##modlearning_selector", new Vector2(0, selectorHeight), true))
        using (var selectorTable = ImRaii.Table("##modlearning_selector_table", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV))
        {
            if (!selectorTable) return;
            ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch, 0.9f);
            ImGui.TableSetupColumn("Job", ImGuiTableColumnFlags.WidthStretch, 0.7f);
            ImGui.TableSetupColumn("Mod", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("Option", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"Character ({_modLearningCharacters.Count})");
            ImGui.Separator();
            using (ImRaii.ListBox("##modlearning_characters", new Vector2(-1, listHeight)))
            {
                lock (_modLearningLock)
                {
                    foreach (var entry in _modLearningCharacters)
                    {
                        if (ImGui.Selectable(entry, string.Equals(_selectedModLearningCharacter, entry, StringComparison.Ordinal)))
                        {
                            _selectedModLearningCharacter = entry;
                            _selectedModLearningMod = string.Empty;
                            _selectedModLearningOption = string.Empty;
                            _selectedModLearningJobId = 0;
                            LoadModLearningStates();
                        }
                    }
                }
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"Job ({jobKeys.Count})");
            ImGui.Separator();
            using (ImRaii.ListBox("##modlearning_jobs", new Vector2(-1, listHeight)))
            {
                foreach (var jobId in jobKeys)
                {
                    var jobName = jobId == 0
                        ? "All Jobs"
                        : (_uiSharedService.JobData.TryGetValue((ushort)jobId, out var jobLabel) ? jobLabel : jobId.ToString());
                    if (ImGui.Selectable(jobName, _selectedModLearningJobId == jobId))
                    {
                        _selectedModLearningJobId = jobId;
                        _selectedModLearningMod = string.Empty;
                        _selectedModLearningOption = string.Empty;
                    }
                }
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"Mod ({modsForJob.Count})");
            ImGui.Separator();
            using (ImRaii.ListBox("##modlearning_mods", new Vector2(-1, listHeight)))
            {
                foreach (var modName in sortedModsForJob)
                {
                    var modDisplayName = GetModDisplayName(modDisplayNames, modName);
                    if (!string.IsNullOrWhiteSpace(_modLearningFilter)
                        && !modDisplayName.Contains(_modLearningFilter, StringComparison.OrdinalIgnoreCase)
                        && !modName.Contains(_modLearningFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var usedByJob = ModUsedByJob(modName, _selectedModLearningJobId);
                    var modRuntime = GetCurrentModRuntimeStatus(modName);
                    var comparison = usedByJob ? GetModComparison(modName, _selectedModLearningJobId, modRuntime) : null;
                    var labelColor = GetModListColor(usedByJob, modRuntime, comparison);
                    var emoteIndicator = emoteOverriddenMods.Contains(modName) ? " • emote overridden" : string.Empty;
                    ImGui.PushStyleColor(ImGuiCol.Text, labelColor);
                    if (ImGui.Selectable($"{modDisplayName}{emoteIndicator}##{modName}", string.Equals(_selectedModLearningMod, modName, StringComparison.Ordinal)))
                    {
                        _selectedModLearningMod = modName;
                        _selectedModLearningOption = string.Empty;
                    }
                    ImGui.PopStyleColor();
                }
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"Option ({options.Count})");
            ImGui.Separator();
            using (ImRaii.ListBox("##modlearning_options", new Vector2(-1, listHeight)))
            {
                foreach (var optionEntry in options)
                {
                    var optionLabel = optionEntry.Key;
                    var state = optionEntry.Value;
                    var isUsedByJob = OptionUsedByJob(state, _selectedModLearningJobId);
                    var runtimeStatus = GetOptionRuntimeStatus(state, isUsedByJob, runtime);
                    var optionColor = runtimeStatus.Color;
                    ImGui.PushStyleColor(ImGuiCol.Text, optionColor);
                    var displayLabel = $"{optionLabel} • {runtimeStatus.Tag}";
                    var selected = string.Equals(_selectedModLearningOption, optionLabel, StringComparison.Ordinal);
                    if (ImGui.Selectable(displayLabel, selected))
                    {
                        _selectedModLearningOption = optionLabel;
                    }
                    ImGui.PopStyleColor();
                }
            }
        }

        ImGuiHelpers.ScaledDummy(8);
        using (ImRaii.Child("##modlearning_content", new Vector2(0, 0), false, ImGuiWindowFlags.HorizontalScrollbar))
        {
            DrawModLearningJsonBuild();

        var selectedState = GetSelectedState();
        if (selectedState == null)
        {
            ImGui.TextUnformatted("Select a character, mod and option to view entries.");
            return;
        }

        var selectedList = GetDisplayListForJob(selectedState, _selectedModLearningJobId);
        var selectedListWithKind = GetDisplayListForJobWithKind(selectedState, _selectedModLearningJobId);
        var expectedKeys = BuildExpectedReplacementKeySet(selectedList);
        var currentKeys = currentKeysSnapshot;
        var keySummary = GetKeyComparisonSummary(expectedKeys, currentKeys);
        var currentEntries = GetCurrentReplacementEntriesSnapshot();
        var optionStatus = GetSelectedModOptionStatus(selectedState);
        var jobStatus = GetSelectedJobMismatchStatus();
        if (currentKeys.Count == 0)
        {
            UiSharedService.ColorTextWrapped("No local character data loaded for comparison.", ImGuiColors.DalamudGrey);
        }
        else
        {
            var summaryColor = keySummary.MissingCount > 0 ? ImGuiColors.DalamudYellow : ImGuiColors.ParsedGreen;
            UiSharedService.ColorTextWrapped($"Comparison: {keySummary.PresentCount}/{keySummary.ExpectedCount} present, {keySummary.MissingCount} missing, {keySummary.ExtraCount} extra",
                summaryColor);
            ImGui.TextUnformatted($"Expected (job) entries: {keySummary.ExpectedCount}");
            ImGui.TextUnformatted($"Current total entries: {currentKeys.Count}");
            if (!string.IsNullOrWhiteSpace(_modLearningCurrentDataHash))
            {
                ImGui.TextUnformatted($"Local Data Hash: {_modLearningCurrentDataHash}");
            }
        }

        var missingEntries = currentKeys.Count > 0
            ? selectedList.Where(replacement => !IsReplacementFullyPresent(replacement, currentKeys)).ToList()
            : [];
        var presentEntries = currentKeys.Count > 0
            ? selectedList.Where(replacement => IsReplacementFullyPresent(replacement, currentKeys)).ToList()
            : selectedList.ToList();
        var missingEntriesWithKind = currentKeys.Count > 0
            ? selectedListWithKind.Where(expected => !IsReplacementFullyPresent(expected.Replacement, currentKeys)).ToList()
            : [];

        using (var table = ImRaii.Table("##modlearning_table", 2, ImGuiTableFlags.RowBg))
        {
            if (table)
            {
                ImGui.TableSetupColumn("Game Paths", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableSetupColumn("Resolved Path", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableHeadersRow();
                foreach (var replacement in selectedList)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    var gamePaths = replacement.GamePaths.Count == 0 ? string.Empty : string.Join(", ", replacement.GamePaths);
                    var present = IsReplacementFullyPresent(replacement, currentKeys);
                    if (!present && currentKeys.Count > 0)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Text, SpheneCustomTheme.Colors.Warning);
                    }
                    ImGui.TextWrapped(gamePaths);
                    if (!present && currentKeys.Count > 0)
                    {
                        ImGui.PopStyleColor();
                    }
                    ImGui.TableNextColumn();
                    if (!present && currentKeys.Count > 0)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Text, SpheneCustomTheme.Colors.Warning);
                    }
                    ImGui.TextWrapped(replacement.ResolvedPath);
                    if (!present && currentKeys.Count > 0)
                    {
                        ImGui.PopStyleColor();
                    }
                }
            }
        }

        ImGuiHelpers.ScaledDummy(6);

        if (currentKeys.Count > 0 && missingEntries.Count > 0)
        {
            var missingNotLoaded = missingEntries.Where(entry => !HasLocalEntriesForPaths(entry, currentEntries)).ToList();
            var missingLoaded = missingEntries.Where(entry => HasLocalEntriesForPaths(entry, currentEntries)).ToList();
            ImGuiHelpers.ScaledDummy(6);
            if (ImGui.CollapsingHeader($"Missing expected entries (loaded): {missingLoaded.Count}", ImGuiTreeNodeFlags.DefaultOpen))
            {
                using (ImRaii.Child("##modlearning_missing_buttons", new Vector2(0, ImGui.GetFrameHeight()), false))
                {
                    if (ImGui.Button("Copy Missing", new Vector2(140 * ImGuiHelpers.GlobalScale, 0)))
                    {
                        var lines = missingLoaded
                            .SelectMany(e => e.GamePaths.Select(gp => $"{gp} -> {e.ResolvedPath}"))
                            .ToArray();
                        ImGui.SetClipboardText(string.Join(Environment.NewLine, lines));
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Copy Missing + Reasons", new Vector2(200 * ImGuiHelpers.GlobalScale, 0)))
                    {
                        var lines = missingLoaded.SelectMany(entry =>
                        {
                            var reason = GetCurrentMismatchInfo(entry, currentEntries);
                            return entry.GamePaths.Select(gp => $"{gp} -> {entry.ResolvedPath} | {reason}");
                        }).ToArray();
                        ImGui.SetClipboardText(string.Join(Environment.NewLine, lines));
                    }
                    ImGui.SameLine();
                    using (ImRaii.Disabled(missingEntriesWithKind.Count == 0))
                    {
                        if (ImGui.Button("Apply Missing", new Vector2(140 * ImGuiHelpers.GlobalScale, 0)))
                        {
                            _logger.LogDebug("[ModLearning] ApplyMissing clicked: totalMissing={totalMissing} candidates={candidates}", missingEntries.Count, missingEntriesWithKind.Count);
                            _ = Task.Run(() => ApplyMissingToLocalCharacterDataAsync(missingEntriesWithKind));
                        }
                    }
                    ImGui.SameLine();
                    ImGui.TextUnformatted($"Apply candidates: {missingEntriesWithKind.Count}");
                }
                var missingHeight = 180f * ImGuiHelpers.GlobalScale;
                using var missingChild = ImRaii.Child("##modlearning_missing", new Vector2(0, missingHeight), true, ImGuiWindowFlags.HorizontalScrollbar);
                if (missingChild)
                {
                    foreach (var entry in missingLoaded)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Text, SpheneCustomTheme.Colors.Warning);
                        ImGui.TextUnformatted(entry.ResolvedPath);
                        ImGui.PopStyleColor();
                        using (ImRaii.PushIndent(12f))
                        {
                            foreach (var gp in entry.GamePaths)
                            {
                                ImGui.TextUnformatted(gp);
                            }
                            var mismatchInfo = GetCurrentMismatchInfo(entry, currentEntries);
                            ImGui.PushStyleColor(ImGuiCol.Text, SpheneCustomTheme.Colors.AccentCyan);
                            ImGui.TextUnformatted(mismatchInfo);
                            ImGui.PopStyleColor();
                            if (optionStatus.HasValue && !optionStatus.Value.IsActive)
                            {
                                ImGui.PushStyleColor(ImGuiCol.Text, optionStatus.Value.Color);
                                ImGui.TextUnformatted(optionStatus.Value.Reason);
                                ImGui.PopStyleColor();
                            }
                            if (jobStatus.HasValue && !jobStatus.Value.IsActive)
                            {
                                ImGui.PushStyleColor(ImGuiCol.Text, jobStatus.Value.Color);
                                ImGui.TextUnformatted(jobStatus.Value.Reason);
                                ImGui.PopStyleColor();
                            }
                        }
                        ImGui.Separator();
                    }
                }
            }

            if (missingNotLoaded.Count > 0)
            {
                ImGuiHelpers.ScaledDummy(6);
                if (ImGui.CollapsingHeader($"Missing expected entries (not loaded): {missingNotLoaded.Count}", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    using (ImRaii.Child("##modlearning_missing_not_loaded_buttons", new Vector2(0, ImGui.GetFrameHeight()), false))
                    {
                        if (ImGui.Button("Redraw & Rebuild", new Vector2(160 * ImGuiHelpers.GlobalScale, 0)))
                        {
                            TriggerLocalRebuild();
                        }
                        ImGui.SameLine();
                        var forceRestoreGlobal = _spheneConfigService.Current.ModLearningForceRestorePersistent;
                        if (ImGui.Checkbox("Always force restore learned entries (persistent)", ref forceRestoreGlobal))
                        {
                            _spheneConfigService.Current.ModLearningForceRestorePersistent = forceRestoreGlobal;
                            _spheneConfigService.Save();
                        }
                        ImGui.SameLine();
                        var forceRestore = IsForceRestoreEnabledForSelectedMod();
                        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_selectedModLearningMod)))
                        {
                            if (ImGui.Checkbox("Force restore this mod (persistent)", ref forceRestore))
                            {
                                SetForceRestoreForSelectedMod(forceRestore);
                            }
                        }
                        ImGui.SameLine();
                        ImGui.TextUnformatted("Try this after changing jobs or triggering the effect once.");
                    }
                    var missingHeight = 180f * ImGuiHelpers.GlobalScale;
                    using var missingChild = ImRaii.Child("##modlearning_missing_not_loaded", new Vector2(0, missingHeight), true, ImGuiWindowFlags.HorizontalScrollbar);
                    if (missingChild)
                    {
                        foreach (var entry in missingNotLoaded)
                        {
                            ImGui.PushStyleColor(ImGuiCol.Text, SpheneCustomTheme.Colors.Warning);
                            ImGui.TextUnformatted(entry.ResolvedPath);
                            ImGui.PopStyleColor();
                            using (ImRaii.PushIndent(12f))
                            {
                                foreach (var gp in entry.GamePaths)
                                {
                                    ImGui.TextUnformatted(gp);
                                }
                                ImGui.PushStyleColor(ImGuiCol.Text, SpheneCustomTheme.Colors.AccentCyan);
                                ImGui.TextUnformatted("Not loaded in current character data.");
                                ImGui.PopStyleColor();
                                if (optionStatus.HasValue && !optionStatus.Value.IsActive)
                                {
                                    ImGui.PushStyleColor(ImGuiCol.Text, optionStatus.Value.Color);
                                    ImGui.TextUnformatted(optionStatus.Value.Reason);
                                    ImGui.PopStyleColor();
                                }
                                if (jobStatus.HasValue && !jobStatus.Value.IsActive)
                                {
                                    ImGui.PushStyleColor(ImGuiCol.Text, jobStatus.Value.Color);
                                    ImGui.TextUnformatted(jobStatus.Value.Reason);
                                    ImGui.PopStyleColor();
                                }
                            }
                            ImGui.Separator();
                        }
                    }
                }
            }
        }

        if (currentKeys.Count > 0 && presentEntries.Count > 0)
        {
            ImGuiHelpers.ScaledDummy(6);
            if (ImGui.CollapsingHeader($"Present entries: {presentEntries.Count}", ImGuiTreeNodeFlags.None))
            {
                var presentHeight = 180f * ImGuiHelpers.GlobalScale;
                using var presentChild = ImRaii.Child("##modlearning_present", new Vector2(0, presentHeight), true, ImGuiWindowFlags.HorizontalScrollbar);
                if (presentChild)
                {
                    foreach (var entry in presentEntries)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Text, SpheneCustomTheme.Colors.Success);
                        ImGui.TextUnformatted(entry.ResolvedPath);
                        ImGui.PopStyleColor();
                        using (ImRaii.PushIndent(12f))
                        {
                            foreach (var gp in entry.GamePaths)
                            {
                                ImGui.TextUnformatted(gp);
                            }
                        }
                        ImGui.Separator();
                    }
                }
            }
        }

        if (currentKeys.Count > 0)
        {
            var extraEntries = currentEntries
                .Where(e => IsCurrentEntryUnexpected(e, expectedKeys))
                .ToList();
            ImGuiHelpers.ScaledDummy(6);
            if (ImGui.CollapsingHeader($"Current-only entries (not expected): {extraEntries.Count}", ImGuiTreeNodeFlags.DefaultOpen))
            {
                var extraHeight = 180f * ImGuiHelpers.GlobalScale;
                using var extraChild = ImRaii.Child("##modlearning_current_only", new Vector2(0, extraHeight), true, ImGuiWindowFlags.HorizontalScrollbar);
                if (extraChild)
                {
                    foreach (var entry in extraEntries)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Text, SpheneCustomTheme.Colors.AccentCyan);
                        var label = string.IsNullOrWhiteSpace(entry.Hash) ? entry.FileSwapPath : entry.Hash;
                        ImGui.TextUnformatted(label);
                        ImGui.PopStyleColor();
                        using (ImRaii.PushIndent(12f))
                        {
                            foreach (var gp in entry.GamePaths)
                            {
                                ImGui.TextUnformatted(gp);
                            }
                        }
                        ImGui.Separator();
                    }
                }
            }
        }
        }
    }

    private void EnsureModLearningLoaded()
    {
        if (_modLearningLoadTask != null && !_modLearningLoadTask.IsCompleted) return;
        if (_modLearningCharacters.Count == 0)
        {
            LoadModLearningStates(forceReload: true);
        }
    }

    private void EnsureModLearningCurrentCharacterSelected()
    {
        if (_modLearningAutoSelected) return;
        if (_modLearningCharacters.Count == 0) return;

        var currentKey = GetCurrentCharacterKey();
        if (string.IsNullOrWhiteSpace(currentKey)) return;

        lock (_modLearningLock)
        {
            if (!_modLearningCharacters.Contains(currentKey, StringComparer.Ordinal)) return;
        }

        if (!string.Equals(_selectedModLearningCharacter, currentKey, StringComparison.Ordinal))
        {
            _selectedModLearningCharacter = currentKey;
            _selectedModLearningMod = string.Empty;
            _selectedModLearningOption = string.Empty;
            _selectedModLearningJobId = 0;
            _modLearningAutoSelected = true;
            LoadModLearningStates(forceReload: true);
        }
    }

    private void LoadModLearningStates(bool forceReload = false)
    {
        if (_modLearningLoadTask != null && !_modLearningLoadTask.IsCompleted && !forceReload) return;
        _modLearningLoading = true;
        var selectedCharacter = _selectedModLearningCharacter;
        _modLearningLoadTask = Task.Run(async () =>
        {
            var characters = await _characterDataSqliteStore.GetLearnedModCharacterKeysAsync().ConfigureAwait(false);
            var states = string.IsNullOrWhiteSpace(selectedCharacter)
                ? new List<LearnedModState>()
                : await _characterDataSqliteStore.GetLearnedModsAsync(selectedCharacter).ConfigureAwait(false);
            var localCharacterData = await _characterDataSqliteStore.GetLocalCharacterDataAsync().ConfigureAwait(false);
            var currentKeys = BuildCurrentReplacementKeySet(localCharacterData);
            var currentReplacements = BuildCurrentReplacementEntries(localCharacterData);
            var currentHash = localCharacterData?.DataHash?.Value ?? string.Empty;
            var grouped = states
                .GroupBy(s => s.ModDirectoryName, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var jsonByMod = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in grouped.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                jsonByMod[entry.Key] = JsonSerializer.Serialize(entry.Value, ModLearningJsonOptions);
            }
            lock (_modLearningLock)
            {
                _modLearningCharacters = characters;
                _modLearningStatesByMod = grouped;
                _modLearningJsonByMod = jsonByMod;
                _modLearningCurrentReplacementKeys = currentKeys;
                _modLearningCurrentReplacements = currentReplacements;
                _modLearningCurrentDataHash = currentHash;
            }
        }).ContinueWith(_ =>
        {
            _modLearningLoading = false;
        }, TaskScheduler.Default);
    }

    private string GetCurrentCharacterKey()
    {
        var playerName = _uiSharedService.PlayerName;
        if (string.IsNullOrWhiteSpace(playerName)) return string.Empty;
        if (!_uiSharedService.WorldData.TryGetValue((ushort)_uiSharedService.WorldId, out var worldName)) return string.Empty;
        return $"{playerName}@{worldName}";
    }

    private static HashSet<string> BuildCurrentReplacementKeySet(Sphene.API.Data.CharacterData? data)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (data == null) return keys;

        foreach (var list in data.FileReplacements.Values)
        {
            if (list == null) continue;
            foreach (var entry in list)
            {
                var hash = entry.Hash ?? string.Empty;
                var swap = entry.FileSwapPath ?? string.Empty;
                var gamePaths = entry.GamePaths ?? Array.Empty<string>();
                foreach (var gp in gamePaths)
                {
                    if (!string.IsNullOrWhiteSpace(hash))
                    {
                        keys.Add(BuildReplacementKey("hash", hash, gp));
                    }
                    else if (!string.IsNullOrWhiteSpace(swap))
                    {
                        keys.Add(BuildReplacementKey("swap", swap, gp));
                    }
                }
            }
        }

        return keys;
    }

    private static List<CurrentReplacement> BuildCurrentReplacementEntries(Sphene.API.Data.CharacterData? data)
    {
        var results = new List<CurrentReplacement>();
        if (data == null) return results;

        foreach (var list in data.FileReplacements.Values)
        {
            if (list == null) continue;
            foreach (var entry in list)
            {
                var gamePaths = entry.GamePaths ?? Array.Empty<string>();
                results.Add(new CurrentReplacement(entry.Hash ?? string.Empty, entry.FileSwapPath ?? string.Empty, gamePaths));
            }
        }

        return results;
    }

    private static string BuildReplacementKey(string prefix, string value, string gamePath)
    {
        return $"{prefix}|{NormalizePathString(value)}|{NormalizePathString(gamePath)}";
    }

    private static string NormalizePathString(string path)
    {
        return path.Replace('\\', '/').ToLowerInvariant();
    }

    private HashSet<string> GetCurrentReplacementKeysSnapshot()
    {
        lock (_modLearningLock)
        {
            return new HashSet<string>(_modLearningCurrentReplacementKeys, StringComparer.OrdinalIgnoreCase);
        }
    }

    private List<CurrentReplacement> GetCurrentReplacementEntriesSnapshot()
    {
        lock (_modLearningLock)
        {
            return _modLearningCurrentReplacements.ToList();
        }
    }

    private static HashSet<string> BuildExpectedReplacementKeySet(List<FileReplacement> replacements)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var replacement in replacements)
        {
            var gamePaths = replacement.GamePaths ?? [];
            if (replacement.IsFileSwap)
            {
                var swap = replacement.ResolvedPath ?? string.Empty;
                foreach (var gp in gamePaths)
                {
                    keys.Add(BuildReplacementKey("swap", swap, gp));
                }
            }
            else
            {
                var hash = replacement.Hash ?? string.Empty;
                foreach (var gp in gamePaths)
                {
                    keys.Add(BuildReplacementKey("hash", hash, gp));
                }
            }
        }
        return keys;
    }

    private static bool IsReplacementFullyPresent(FileReplacement replacement, HashSet<string> currentKeys)
    {
        if (currentKeys.Count == 0) return true;
        var gamePaths = replacement.GamePaths ?? [];
        if (replacement.IsFileSwap)
        {
            var swap = replacement.ResolvedPath ?? string.Empty;
            foreach (var gp in gamePaths)
            {
                if (!currentKeys.Contains(BuildReplacementKey("swap", swap, gp)))
                {
                    return false;
                }
            }
            return true;
        }

        var hash = replacement.Hash ?? string.Empty;
        foreach (var gp in gamePaths)
        {
            if (!currentKeys.Contains(BuildReplacementKey("hash", hash, gp)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsCurrentEntryUnexpected(CurrentReplacement entry, HashSet<string> expectedKeys)
    {
        if (expectedKeys.Count == 0) return false;
        var gamePaths = entry.GamePaths ?? Array.Empty<string>();
        if (!string.IsNullOrWhiteSpace(entry.Hash))
        {
            foreach (var gp in gamePaths)
            {
                if (!expectedKeys.Contains(BuildReplacementKey("hash", entry.Hash, gp)))
                {
                    return true;
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(entry.FileSwapPath))
        {
            foreach (var gp in gamePaths)
            {
                if (!expectedKeys.Contains(BuildReplacementKey("swap", entry.FileSwapPath, gp)))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static string GetCurrentMismatchInfo(FileReplacement expected, List<CurrentReplacement> currentEntries)
    {
        if (currentEntries.Count == 0)
        {
            return "Local character data has no current entries.";
        }

        var expectedPaths = expected.GamePaths.Select(NormalizePathString).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (expected.IsFileSwap)
        {
            var swap = NormalizePathString(expected.ResolvedPath ?? string.Empty);
            var matching = currentEntries.Where(e =>
                string.Equals(NormalizePathString(e.FileSwapPath ?? string.Empty), swap, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matching.Count == 0)
            {
                var pathMatches = currentEntries.Where(e =>
                        e.GamePaths.Any(p => expectedPaths.Contains(NormalizePathString(p))))
                    .ToList();
                if (pathMatches.Count > 0)
                {
                    var swaps = pathMatches.Select(e => e.FileSwapPath ?? string.Empty)
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(3);
                    var hashes = pathMatches.Select(e => e.Hash ?? string.Empty)
                        .Where(h => !string.IsNullOrWhiteSpace(h))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(3);
                    return $"No local swap match for: {swap} (paths exist; swaps: {string.Join(", ", swaps)} hashes: {string.Join(", ", hashes)})";
                }
                return $"No local swap match for: {swap} (no local entries for paths)";
            }
            var currentPaths = matching.SelectMany(e => e.GamePaths ?? Array.Empty<string>())
                .Select(NormalizePathString)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = expectedPaths.Where(p => !currentPaths.Contains(p)).Take(5).ToList();
            return missing.Count > 0
                ? $"Local swap exists, missing paths: {string.Join(", ", missing)}"
                : "Local swap exists with matching paths";
        }

        var hash = expected.Hash ?? string.Empty;
        if (string.IsNullOrWhiteSpace(hash))
        {
            return "Expected hash is empty.";
        }
        var hashMatches = currentEntries.Where(e => string.Equals(e.Hash, hash, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hashMatches.Count == 0)
        {
            var pathMatches = currentEntries.Where(e =>
                    e.GamePaths.Any(p => expectedPaths.Contains(NormalizePathString(p))))
                .ToList();
            if (pathMatches.Count > 0)
            {
                var hashes = pathMatches.Select(e => e.Hash ?? string.Empty)
                    .Where(h => !string.IsNullOrWhiteSpace(h))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(3);
                var swaps = pathMatches.Select(e => e.FileSwapPath ?? string.Empty)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(3);
                return $"No local hash match for: {hash} (paths exist; hashes: {string.Join(", ", hashes)} swaps: {string.Join(", ", swaps)})";
            }
            return $"No local hash match for: {hash} (no local entries for paths)";
        }
        var hashPaths = hashMatches.SelectMany(e => e.GamePaths ?? Array.Empty<string>())
            .Select(NormalizePathString)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingHashPaths = expectedPaths.Where(p => !hashPaths.Contains(p)).Take(5).ToList();
        return missingHashPaths.Count > 0
            ? $"Local hash exists, missing paths: {string.Join(", ", missingHashPaths)}"
            : "Local hash exists with matching paths";
    }

    private static bool HasLocalEntriesForPaths(FileReplacement expected, List<CurrentReplacement> currentEntries)
    {
        if (currentEntries.Count == 0) return false;
        var expectedPaths = expected.GamePaths.Select(NormalizePathString).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return currentEntries.Any(e => e.GamePaths.Any(p => expectedPaths.Contains(NormalizePathString(p))));
    }

    private readonly record struct ModOptionStatus(bool IsActive, string Reason, Vector4 Color);
    private readonly record struct ModRuntimeStatus(bool Enabled, Dictionary<string, List<string>> Settings);

    private ModOptionStatus? GetSelectedModOptionStatus(LearnedModState selectedState)
    {
        if (string.IsNullOrWhiteSpace(_selectedModLearningMod)) return null;
        if (!_ipcManager.Penumbra.APIAvailable) return null;
        var (valid, _, collection) = _ipcManager.Penumbra.GetCollectionForObject(0);
        if (!valid) return null;
        var (ec, settingsTuple) = _ipcManager.Penumbra.GetModSettings(collection.Id, _selectedModLearningMod);
        if (ec != Penumbra.Api.Enums.PenumbraApiEc.Success || settingsTuple == null) return null;
        var (enabled, _, settings, _) = settingsTuple.Value;
        if (!enabled)
        {
            return new ModOptionStatus(false, "Mod disabled in current collection.", GetSubtleErrorColor());
        }
        var currentSettings = settings.ToDictionary(k => k.Key, k => k.Value.ToList(), StringComparer.Ordinal);
        if (!SettingsSubsetMatch(currentSettings, selectedState.Settings))
        {
            var reason = BuildOptionDisabledReason(currentSettings, selectedState.Settings);
            return new ModOptionStatus(false, reason, GetSubtleErrorColor());
        }
        return new ModOptionStatus(true, string.Empty, Vector4.Zero);
    }

    private ModOptionStatus? GetSelectedJobMismatchStatus()
    {
        if (_selectedModLearningJobId == 0) return null;
        var currentJobId = _dalamudUtilService.ClassJobId;
        if (currentJobId == 0) return null;
        if (_selectedModLearningJobId == currentJobId) return null;
        var jobLabel = _uiSharedService.JobData.TryGetValue(currentJobId, out var name) ? name : currentJobId.ToString();
        return new ModOptionStatus(false, $"Job mismatch (current: {jobLabel}).", GetSubtleErrorColor());
    }

    private static bool SettingsSubsetMatch(Dictionary<string, List<string>> current, Dictionary<string, List<string>> state)
    {
        if (state.Count == 0) return true;
        foreach (var kvp in state)
        {
            if (!current.TryGetValue(kvp.Key, out var currentValues)) return false;
            var currentSet = currentValues.ToHashSet(StringComparer.Ordinal);
            foreach (var value in kvp.Value)
            {
                if (!currentSet.Contains(value)) return false;
            }
        }
        return true;
    }

    private static Vector4 GetSubtleErrorColor()
    {
        var c = SpheneCustomTheme.Colors.Error;
        return new Vector4(c.X, c.Y, c.Z, 0.6f);
    }

    private static string BuildOptionDisabledReason(Dictionary<string, List<string>> currentSettings, Dictionary<string, List<string>> expectedSettings)
    {
        foreach (var kvp in expectedSettings)
        {
            if (!currentSettings.TryGetValue(kvp.Key, out var currentValues))
            {
                return $"Option disabled (group {kvp.Key} inactive).";
            }
            var expectedSet = kvp.Value.ToHashSet(StringComparer.Ordinal);
            if (!currentValues.Any(v => expectedSet.Contains(v)))
            {
                var expected = kvp.Value.Count == 0 ? "none" : string.Join(", ", kvp.Value);
                var current = currentValues.Count == 0 ? "none" : string.Join(", ", currentValues);
                return $"Option disabled in group {kvp.Key} (expected: {expected}, current: {current}).";
            }
        }
        var currentLabel = FormatSettingsLabel(currentSettings);
        return $"Option mismatch (current: {currentLabel}).";
    }

    private ModRuntimeStatus GetCurrentModRuntimeStatus(string modName)
    {
        if (string.IsNullOrWhiteSpace(modName)) return new ModRuntimeStatus(false, new Dictionary<string, List<string>>(StringComparer.Ordinal));
        if (!_ipcManager.Penumbra.APIAvailable) return new ModRuntimeStatus(false, new Dictionary<string, List<string>>(StringComparer.Ordinal));
        var (valid, _, collection) = _ipcManager.Penumbra.GetCollectionForObject(0);
        if (!valid) return new ModRuntimeStatus(false, new Dictionary<string, List<string>>(StringComparer.Ordinal));
        var (ec, settingsTuple) = _ipcManager.Penumbra.GetModSettings(collection.Id, modName);
        if (ec != Penumbra.Api.Enums.PenumbraApiEc.Success || settingsTuple == null)
        {
            return new ModRuntimeStatus(false, new Dictionary<string, List<string>>(StringComparer.Ordinal));
        }
        var (enabled, _, settings, _) = settingsTuple.Value;
        if (!enabled)
        {
            return new ModRuntimeStatus(false, new Dictionary<string, List<string>>(StringComparer.Ordinal));
        }
        return new ModRuntimeStatus(true, settings.ToDictionary(k => k.Key, k => k.Value.ToList(), StringComparer.Ordinal));
    }

    private static Vector4 GetModListColor(bool usedByJob, ModRuntimeStatus runtime, (Vector4 Color, int TotalCount, int MissingCount)? comparison)
    {
        if (!usedByJob) return SpheneCustomTheme.Colors.TextSecondary;
        if (!runtime.Enabled)
        {
            return GetSubtleErrorColor();
        }
        return comparison?.MissingCount == 0 ? SpheneCustomTheme.Colors.Success : (comparison?.Color ?? SpheneCustomTheme.Colors.SpheneGold);
    }

    private readonly record struct OptionRuntimeStatus(string Tag, Vector4 Color);

    private static OptionRuntimeStatus GetOptionRuntimeStatus(LearnedModState state, bool isUsedByJob, ModRuntimeStatus runtime)
    {
        if (!isUsedByJob) return new OptionRuntimeStatus("off", SpheneCustomTheme.Colors.TextSecondary);
        if (!runtime.Enabled) return new OptionRuntimeStatus("disabled", GetSubtleErrorColor());
        if (runtime.Settings.Count > 0 && SettingsSubsetMatch(runtime.Settings, state.Settings))
        {
            return new OptionRuntimeStatus("active", SpheneCustomTheme.Colors.Success);
        }
        if (IsOptionDisabledByGroupSelection(runtime.Settings, state.Settings))
        {
            return new OptionRuntimeStatus("disabled", GetSubtleErrorColor());
        }
        return new OptionRuntimeStatus("job", SpheneCustomTheme.Colors.SpheneGold);
    }

    private static bool IsOptionDisabledByGroupSelection(Dictionary<string, List<string>> currentSettings, Dictionary<string, List<string>> expectedSettings)
    {
        foreach (var kvp in expectedSettings)
        {
            if (!currentSettings.TryGetValue(kvp.Key, out var currentValues)) return true;
            var expectedSet = kvp.Value.ToHashSet(StringComparer.Ordinal);
            if (!currentValues.Any(v => expectedSet.Contains(v)))
            {
                return true;
            }
        }
        return false;
    }

    private static (int ExpectedCount, int PresentCount, int MissingCount, int ExtraCount) GetKeyComparisonSummary(HashSet<string> expectedKeys, HashSet<string> currentKeys)
    {
        if (expectedKeys.Count == 0) return (0, 0, 0, currentKeys.Count);
        var present = expectedKeys.Count(key => currentKeys.Contains(key));
        var missing = expectedKeys.Count - present;
        var extra = currentKeys.Count - present;
        return (expectedKeys.Count, present, missing, extra);
    }

    private (Vector4 Color, int TotalCount, int MissingCount)? GetModComparison(string modName, uint jobId, ModRuntimeStatus runtime)
    {
        HashSet<string> currentKeys;
        Dictionary<string, List<LearnedModState>> statesByMod;
        lock (_modLearningLock)
        {
            currentKeys = new HashSet<string>(_modLearningCurrentReplacementKeys, StringComparer.OrdinalIgnoreCase);
            statesByMod = _modLearningStatesByMod;
        }

        if (currentKeys.Count == 0)
        {
            return null;
        }

        if (!statesByMod.TryGetValue(modName, out var states) || states.Count == 0)
        {
            return null;
        }

        var learnedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var relevantStates = states;
        if (runtime.Enabled && runtime.Settings.Count > 0)
        {
            var matchingStates = states.Where(s => SettingsSubsetMatch(runtime.Settings, s.Settings)).ToList();
            if (matchingStates.Count > 0)
            {
                relevantStates = matchingStates;
            }
        }

        foreach (var state in relevantStates)
        {
            var list = GetDisplayListForJob(state, jobId);
            foreach (var replacement in list)
            {
                foreach (var gp in replacement.GamePaths)
                {
                    if (replacement.IsFileSwap)
                    {
                        learnedKeys.Add(BuildReplacementKey("swap", replacement.ResolvedPath, gp));
                    }
                    else
                    {
                        learnedKeys.Add(BuildReplacementKey("hash", replacement.Hash, gp));
                    }
                }
            }
        }

        var total = learnedKeys.Count;
        if (total == 0)
        {
            return null;
        }

        var missing = learnedKeys.Count(key => !currentKeys.Contains(key));
        var color = missing > 0 ? SpheneCustomTheme.Colors.Warning : SpheneCustomTheme.Colors.Success;
        return (color, total, missing);
    }

    private HashSet<string> GetEmoteOverriddenModsForJob(IEnumerable<string> modsForJob, uint jobId, HashSet<string> currentKeys)
    {
        if (currentKeys.Count == 0) return [];
        Dictionary<string, List<LearnedModState>> statesByMod;
        lock (_modLearningLock)
        {
            statesByMod = _modLearningStatesByMod;
        }

        var emoteOwners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var emoteMissingByMod = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var modName in modsForJob)
        {
            if (!statesByMod.TryGetValue(modName, out var states) || states.Count == 0) continue;
            var runtime = GetCurrentModRuntimeStatus(modName);
            if (!runtime.Enabled) continue;

            var relevantStates = states;
            if (runtime.Settings.Count > 0)
            {
                var matchingStates = states.Where(s => SettingsSubsetMatch(runtime.Settings, s.Settings)).ToList();
                if (matchingStates.Count > 0)
                {
                    relevantStates = matchingStates;
                }
            }

            foreach (var state in relevantStates)
            {
                if (state.PapEmotes == null || state.PapEmotes.Count == 0) continue;
                var emoteReplacements = GetDisplayListForJob(state, jobId)
                    .Where(replacement => replacement.GamePaths.Any(gp => state.PapEmotes.ContainsKey(NormalizePathString(gp))))
                    .ToList();
                foreach (var replacement in emoteReplacements)
                {
                    var present = IsReplacementFullyPresent(replacement, currentKeys);
                    foreach (var gp in replacement.GamePaths)
                    {
                        var normalized = NormalizePathString(gp);
                        if (!state.PapEmotes.TryGetValue(normalized, out var emoteNames) || string.IsNullOrWhiteSpace(emoteNames)) continue;
                        var names = emoteNames.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        foreach (var emoteName in names)
                        {
                            if (present)
                            {
                                if (!emoteOwners.TryGetValue(emoteName, out var owners))
                                {
                                    owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                    emoteOwners[emoteName] = owners;
                                }
                                owners.Add(modName);
                            }
                            else
                            {
                                if (!emoteMissingByMod.TryGetValue(modName, out var missing))
                                {
                                    missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                    emoteMissingByMod[modName] = missing;
                                }
                                missing.Add(emoteName);
                            }
                        }
                    }
                }
            }
        }

        var overridden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var missingEntry in emoteMissingByMod)
        {
            foreach (var emoteName in missingEntry.Value)
            {
                if (!emoteOwners.TryGetValue(emoteName, out var owners)) continue;
                if (owners.Any(owner => !string.Equals(owner, missingEntry.Key, StringComparison.OrdinalIgnoreCase)))
                {
                    overridden.Add(missingEntry.Key);
                    break;
                }
            }
        }
        return overridden;
    }

    private bool ModUsedByJob(string modName, uint jobId)
    {
        lock (_modLearningLock)
        {
            if (!_modLearningStatesByMod.TryGetValue(modName, out var states)) return false;
            return states.Any(state => OptionUsedByJob(state, jobId));
        }
    }

    private void TriggerLocalRebuild()
    {
        var traceId = $"MLRB-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        _logger.LogDebug("[Trace:ForceRebuild:{trace}] UI_REQUEST", traceId);
        _ = _ipcManager.Penumbra.RedrawPlayerAsync();
        Mediator.Publish(new ForceLocalCharacterDataRebuildMessage(ObjectKind.Player, traceId));
        LoadModLearningStates(forceReload: true);
    }

    private bool IsForceRestoreEnabledForSelectedMod()
    {
        if (string.IsNullOrWhiteSpace(_selectedModLearningMod)) return false;
        var key = GetSelectedModLearningCharacterKey();
        if (string.IsNullOrWhiteSpace(key)) return false;
        var forced = _spheneConfigService.Current.ModLearningForceRestoreMods;
        if (!forced.TryGetValue(key, out var mods)) return false;
        return mods.Any(m => string.Equals(m, _selectedModLearningMod, StringComparison.OrdinalIgnoreCase));
    }

    private void SetForceRestoreForSelectedMod(bool enabled)
    {
        if (string.IsNullOrWhiteSpace(_selectedModLearningMod)) return;
        var key = GetSelectedModLearningCharacterKey();
        if (string.IsNullOrWhiteSpace(key)) return;
        var forced = _spheneConfigService.Current.ModLearningForceRestoreMods;
        if (!forced.TryGetValue(key, out var mods))
        {
            mods = [];
            forced[key] = mods;
        }

        if (enabled)
        {
            if (!mods.Any(m => string.Equals(m, _selectedModLearningMod, StringComparison.OrdinalIgnoreCase)))
            {
                mods.Add(_selectedModLearningMod);
            }
        }
        else
        {
            mods.RemoveAll(m => string.Equals(m, _selectedModLearningMod, StringComparison.OrdinalIgnoreCase));
            if (mods.Count == 0)
            {
                forced.Remove(key);
            }
        }
        _spheneConfigService.Save();
    }

    private string GetSelectedModLearningCharacterKey()
    {
        if (!string.IsNullOrWhiteSpace(_selectedModLearningCharacter))
        {
            return _selectedModLearningCharacter;
        }
        return GetCurrentCharacterKey();
    }

    private async Task ApplyMissingToLocalCharacterDataAsync(List<ExpectedReplacement> missingEntries)
    {
        if (missingEntries.Count == 0) return;

        var localData = await _characterDataSqliteStore.GetLocalCharacterDataAsync().ConfigureAwait(false);
        if (localData == null) return;

        var updatedData = CloneCharacterData(localData);
        var anyAdded = false;
        var addedNew = 0;
        var mergedPaths = 0;
        var skippedNoChange = 0;
        foreach (var entry in missingEntries)
        {
            if (!updatedData.FileReplacements.TryGetValue(entry.Kind, out var list))
            {
                list = [];
                updatedData.FileReplacements[entry.Kind] = list;
            }

            var result = AddOrMergeFileReplacement(list, entry.Replacement);
            if (result.Changed)
            {
                anyAdded = true;
                if (result.AddedNew) addedNew++;
                mergedPaths += result.AddedPaths;
            }
            else
            {
                skippedNoChange++;
            }
        }

        if (!anyAdded) return;

        _logger.LogDebug("[ModLearning] ApplyMissing: total={total} addedNew={addedNew} mergedPaths={mergedPaths} skippedNoChange={skipped}",
            missingEntries.Count, addedNew, mergedPaths, skippedNoChange);
        await _characterDataSqliteStore.UpsertLocalCharacterDataAsync(updatedData).ConfigureAwait(false);
        Mediator.Publish(new CharacterDataReadyForPushMessage(updatedData));
        LoadModLearningStates(forceReload: true);
    }

    private static Sphene.API.Data.CharacterData CloneCharacterData(Sphene.API.Data.CharacterData source)
    {
        var clone = new Sphene.API.Data.CharacterData
        {
            CustomizePlusData = new Dictionary<ObjectKind, string>(source.CustomizePlusData),
            GlamourerData = new Dictionary<ObjectKind, string>(source.GlamourerData),
            ManipulationData = source.ManipulationData,
            HeelsData = source.HeelsData,
            HonorificData = source.HonorificData,
            MoodlesData = source.MoodlesData,
            PetNamesData = source.PetNamesData,
            BypassEmoteData = source.BypassEmoteData,
        };

        var replacements = new Dictionary<ObjectKind, List<FileReplacementData>>();
        foreach (var kvp in source.FileReplacements)
        {
            var list = kvp.Value ?? [];
            replacements[kvp.Key] = list.Select(entry => new FileReplacementData
            {
                Hash = entry.Hash ?? string.Empty,
                FileSwapPath = entry.FileSwapPath ?? string.Empty,
                GamePaths = entry.GamePaths?.ToArray() ?? Array.Empty<string>(),
            }).ToList();
        }
        clone.FileReplacements = replacements;
        return clone;
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct ApplyMissingResult(bool Changed, bool AddedNew, int AddedPaths);

    private static ApplyMissingResult AddOrMergeFileReplacement(List<FileReplacementData> list, FileReplacement replacement)
    {
        var dto = replacement.ToFileReplacementDto();
        var hash = dto.Hash ?? string.Empty;
        var swap = dto.FileSwapPath ?? string.Empty;
        var existing = list.FirstOrDefault(e =>
            string.Equals(e.Hash ?? string.Empty, hash, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.FileSwapPath ?? string.Empty, swap, StringComparison.OrdinalIgnoreCase));

        var gamePaths = dto.GamePaths ?? Array.Empty<string>();
        if (existing != null)
        {
            var merged = new HashSet<string>(existing.GamePaths ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var beforeCount = merged.Count;
            foreach (var gp in gamePaths)
            {
                merged.Add(gp);
            }
            existing.GamePaths = merged.ToArray();
            return new ApplyMissingResult(merged.Count > beforeCount, false, Math.Max(0, merged.Count - beforeCount));
        }

        list.Add(new FileReplacementData
        {
            Hash = hash,
            FileSwapPath = swap,
            GamePaths = gamePaths.ToArray()
        });
        return new ApplyMissingResult(true, true, gamePaths.Length);
    }

    private void DrawModLearningJsonBuild()
    {
        if (!ImGui.CollapsingHeader("Mod Learning JSON Build", ImGuiTreeNodeFlags.DefaultOpen)) return;

        ImGui.Checkbox("Show JSON", ref _showModLearningJson);
        if (!_showModLearningJson) return;

        Dictionary<string, string> snapshot;
        lock (_modLearningLock)
        {
            snapshot = new Dictionary<string, string>(_modLearningJsonByMod, StringComparer.Ordinal);
        }

        if (snapshot.Count == 0)
        {
            ImGui.TextUnformatted("No mod learning data loaded.");
            return;
        }
        var modDisplayNames = GetModDisplayNamesSnapshot();

        var height = 260f * ImGuiHelpers.GlobalScale;
        using var child = ImRaii.Child("##modlearning_json_build", new Vector2(0, height), true, ImGuiWindowFlags.HorizontalScrollbar);
        if (!child) return;

        foreach (var entry in snapshot.OrderBy(k => GetModDisplayName(modDisplayNames, k.Key), StringComparer.OrdinalIgnoreCase))
        {
            var color = GetModColor(entry.Key);
            using var id = ImRaii.PushId(entry.Key);
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            var label = GetModDisplayName(modDisplayNames, entry.Key);
            var open = ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.Framed | ImGuiTreeNodeFlags.AllowItemOverlap);
            ImGui.PopStyleColor();
            ImGui.SetItemAllowOverlap();
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy JSON"))
            {
                ImGui.SetClipboardText(entry.Value);
            }

            if (open)
            {
                using (ImRaii.PushIndent(12f))
                {
                    var lines = entry.Value.Split('\n');
                    foreach (var line in lines)
                    {
                        ImGui.TextUnformatted(line);
                    }
                }
                ImGui.TreePop();
            }

            ImGui.PushStyleColor(ImGuiCol.Separator, color);
            ImGui.Separator();
            ImGui.PopStyleColor();
        }
    }

    private static Vector4 GetModColor(string modName)
    {
        var colors = new[]
        {
            SpheneCustomTheme.Colors.AccentBlue,
            SpheneCustomTheme.Colors.AccentCyan,
            SpheneCustomTheme.Colors.Success,
            SpheneCustomTheme.Colors.Warning,
            SpheneCustomTheme.Colors.Error,
            SpheneCustomTheme.Colors.SpheneGold,
            SpheneCustomTheme.Colors.EtherealGlow,
        };
        var hash = 17;
        foreach (var c in modName)
        {
            hash = (hash * 31) + c;
        }
        var index = Math.Abs(hash) % colors.Length;
        return colors[index];
    }

    private static string FormatSettingsLabel(Dictionary<string, List<string>> settings)
    {
        if (settings.Count == 0) return "Base";
        var parts = settings
            .OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kvp => $"{kvp.Key}: {string.Join(", ", kvp.Value)}");
        return string.Join(" | ", parts);
    }

    private static Dictionary<uint, List<FileReplacement>> BuildJobFileMap(LearnedModState state)
    {
        var result = new Dictionary<uint, List<FileReplacement>>();
        foreach (var fragmentEntry in state.Fragments)
        {
            var fragment = fragmentEntry.Value;
            if (fragment?.FileReplacements != null)
            {
                foreach (var replacement in fragment.FileReplacements)
                {
                    AddReplacement(result, 0, replacement);
                }
            }

            if (fragment?.JobFileReplacements == null) continue;
            foreach (var jobEntry in fragment.JobFileReplacements)
            {
                foreach (var replacement in jobEntry.Value)
                {
                    AddReplacement(result, jobEntry.Key, replacement);
                }
            }
        }

        return result;
    }

    private static Dictionary<uint, List<ExpectedReplacement>> BuildJobFileMapWithKind(LearnedModState state)
    {
        var result = new Dictionary<uint, List<ExpectedReplacement>>();
        foreach (var fragmentEntry in state.Fragments)
        {
            var kind = fragmentEntry.Key;
            var fragment = fragmentEntry.Value;
            if (fragment?.FileReplacements != null)
            {
                foreach (var replacement in fragment.FileReplacements)
                {
                    AddReplacementWithKind(result, 0, kind, replacement);
                }
            }

            if (fragment?.JobFileReplacements == null) continue;
            foreach (var jobEntry in fragment.JobFileReplacements)
            {
                foreach (var replacement in jobEntry.Value)
                {
                    AddReplacementWithKind(result, jobEntry.Key, kind, replacement);
                }
            }
        }

        return result;
    }

    private List<uint> GetJobsForSelectedCharacter()
    {
        var jobs = new HashSet<uint> { 0 };
        lock (_modLearningLock)
        {
            foreach (var modEntry in _modLearningStatesByMod.Values)
            {
                foreach (var state in modEntry)
                {
                    foreach (var fragmentEntry in state.Fragments.Values)
                    {
                        if (fragmentEntry?.JobFileReplacements == null) continue;
                        foreach (var jobId in fragmentEntry.JobFileReplacements.Keys)
                        {
                            jobs.Add(jobId);
                        }
                    }
                }
            }
        }

        return jobs.OrderBy(k => k == 0 ? uint.MinValue : k).ToList();
    }

    private Dictionary<string, string> GetModDisplayNamesSnapshot()
    {
        if (!_ipcManager.Penumbra.APIAvailable) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var mods = _ipcManager.Penumbra.GetMods();
            return mods.ToDictionary(
                k => k.Key,
                v => string.IsNullOrWhiteSpace(v.Value) ? v.Key : v.Value,
                StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string GetModDisplayName(IReadOnlyDictionary<string, string> displayNamesByFolder, string modFolder)
    {
        if (string.IsNullOrWhiteSpace(modFolder)) return string.Empty;
        return displayNamesByFolder.TryGetValue(modFolder, out var displayName) ? displayName : modFolder;
    }

    private List<string> GetModsForSelectedJob()
    {
        Dictionary<string, List<LearnedModState>> mods;
        lock (_modLearningLock)
        {
            mods = _modLearningStatesByMod;
        }

        var result = new List<string>();
        foreach (var modEntry in mods)
        {
            if (_selectedModLearningJobId == 0)
            {
                if (modEntry.Value.Any(OptionUsedGlobally))
                {
                    result.Add(modEntry.Key);
                }
                continue;
            }

            var usedGlobally = modEntry.Value.Any(OptionUsedGlobally);
            if (usedGlobally)
            {
                continue;
            }

            if (modEntry.Value.Any(state => OptionUsedBySpecificJob(state, _selectedModLearningJobId)))
            {
                result.Add(modEntry.Key);
            }
        }

        return result.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private Dictionary<string, LearnedModState> GetOptionsForSelectedMod()
    {
        if (string.IsNullOrWhiteSpace(_selectedModLearningMod)) return new Dictionary<string, LearnedModState>(StringComparer.Ordinal);
        lock (_modLearningLock)
        {
            if (!_modLearningStatesByMod.TryGetValue(_selectedModLearningMod, out var states) || states.Count == 0)
            {
                return new Dictionary<string, LearnedModState>(StringComparer.Ordinal);
            }
            return states
                .OrderBy(s => FormatSettingsLabel(s.Settings), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(s => FormatSettingsLabel(s.Settings), s => s, StringComparer.Ordinal);
        }
    }

    private LearnedModState? GetSelectedState()
    {
        if (string.IsNullOrWhiteSpace(_selectedModLearningMod) || string.IsNullOrWhiteSpace(_selectedModLearningOption))
        {
            return null;
        }

        var options = GetOptionsForSelectedMod();
        return options.TryGetValue(_selectedModLearningOption, out var state) ? state : null;
    }

    private static bool OptionUsedByJob(LearnedModState state, uint jobId)
    {
        foreach (var fragmentEntry in state.Fragments.Values)
        {
            if (fragmentEntry?.FileReplacements != null && fragmentEntry.FileReplacements.Count > 0)
            {
                return true;
            }

            if (jobId != 0 && fragmentEntry?.JobFileReplacements != null && fragmentEntry.JobFileReplacements.ContainsKey(jobId))
            {
                return true;
            }
        }

        return false;
    }

    private static bool OptionUsedGlobally(LearnedModState state)
    {
        foreach (var fragmentEntry in state.Fragments.Values)
        {
            if (fragmentEntry?.FileReplacements != null && fragmentEntry.FileReplacements.Count > 0)
            {
                return true;
            }
        }
        return false;
    }

    private static bool OptionUsedBySpecificJob(LearnedModState state, uint jobId)
    {
        if (jobId == 0) return false;
        foreach (var fragmentEntry in state.Fragments.Values)
        {
            if (fragmentEntry?.JobFileReplacements != null && fragmentEntry.JobFileReplacements.ContainsKey(jobId))
            {
                return true;
            }
        }
        return false;
    }

    private static List<FileReplacement> GetDisplayListForJob(LearnedModState state, uint jobId)
    {
        var jobMap = BuildJobFileMap(state);
        if (jobId == 0)
        {
            return jobMap.TryGetValue(0, out var globalList) ? globalList : [];
        }

        var combined = new List<FileReplacement>();
        if (jobMap.TryGetValue(0, out var global))
        {
            combined.AddRange(global);
        }
        if (jobMap.TryGetValue(jobId, out var jobList))
        {
            combined.AddRange(jobList);
        }

        return combined;
    }

    private static List<ExpectedReplacement> GetDisplayListForJobWithKind(LearnedModState state, uint jobId)
    {
        var jobMap = BuildJobFileMapWithKind(state);
        if (jobId == 0)
        {
            return jobMap.TryGetValue(0, out var globalList) ? globalList : [];
        }

        var combined = new List<ExpectedReplacement>();
        if (jobMap.TryGetValue(0, out var global))
        {
            combined.AddRange(global);
        }
        if (jobMap.TryGetValue(jobId, out var jobList))
        {
            combined.AddRange(jobList);
        }

        return combined;
    }

    private static void AddReplacement(Dictionary<uint, List<FileReplacement>> map, uint jobId, FileReplacement replacement)
    {
        if (!map.TryGetValue(jobId, out var list))
        {
            list = [];
            map[jobId] = list;
        }
        if (list.Any(existing => string.Equals(existing.Hash, replacement.Hash, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        list.Add(replacement);
    }

    private static void AddReplacementWithKind(Dictionary<uint, List<ExpectedReplacement>> map, uint jobId, ObjectKind kind, FileReplacement replacement)
    {
        if (!map.TryGetValue(jobId, out var list))
        {
            list = [];
            map[jobId] = list;
        }
        var key = string.IsNullOrWhiteSpace(replacement.Hash) ? replacement.ResolvedPath : replacement.Hash;
        if (list.Any(existing => string.Equals(string.IsNullOrWhiteSpace(existing.Replacement.Hash) ? existing.Replacement.ResolvedPath : existing.Replacement.Hash,
                key, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        list.Add(new ExpectedReplacement(kind, replacement));
    }

    private void DrawStoredData()
    {
        UiSharedService.DrawTree("What is this? (Explanation / Help)", () =>
        {
            UiSharedService.TextWrapped("This tab allows you to see which transient files are attached to your character.");
            UiSharedService.TextWrapped("Transient files are files that cannot be resolved to your character permanently. Sphene gathers these files in the background while you execute animations, VFX, sound effects, etc.");
            UiSharedService.TextWrapped("When sending your character data to others, Sphene will combine the files listed in \"All Jobs\" and the corresponding currently used job.");
            UiSharedService.TextWrapped("The purpose of this tab is primarily informational for you to see which files you are carrying with you. You can remove added game paths, however if you are using the animations etc. again, "
                + "Sphene will automatically attach these after using them. If you disable associated mods in Penumbra, the associated entries here will also be deleted automatically.");
        });

        ImGuiHelpers.ScaledDummy(5);

        var config = _transientConfigService.Current.TransientConfigs;
        Vector2 availableContentRegion = Vector2.Zero;
        var listHeight = 220f * ImGuiHelpers.GlobalScale;
        using (ImRaii.Group())
        {
            ImGui.TextUnformatted("Character");
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(3);
            availableContentRegion = ImGui.GetContentRegionAvail();
            using (ImRaii.ListBox("##characters", new Vector2(200, Math.Min(listHeight, availableContentRegion.Y))))
            {
                foreach (var entry in config)
                {
                    var name = entry.Key.Split("_");
                    if (!_uiSharedService.WorldData.TryGetValue(ushort.Parse(name[1]), out var worldname))
                    {
                        continue;
                    }
                    if (ImGui.Selectable(name[0] + " (" + worldname + ")", string.Equals(_selectedStoredCharacter, entry.Key, StringComparison.Ordinal)))
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
        bool selectedData = config.TryGetValue(_selectedStoredCharacter, out var transientStorage) && transientStorage != null;
        using (ImRaii.Group())
        {
            ImGui.TextUnformatted("Job");
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(3);
            using (ImRaii.ListBox("##data", new Vector2(150, Math.Min(listHeight, availableContentRegion.Y))))
            {
                if (selectedData)
                {
                    if (ImGui.Selectable("All Jobs", string.Equals(_selectedJobEntry, "alljobs", StringComparison.Ordinal)))
                    {
                        _selectedJobEntry = "alljobs";
                    }
                    foreach (var job in transientStorage!.JobSpecificCache)
                    {
                        if (!_uiSharedService.JobData.TryGetValue(job.Key, out var jobName)) continue;
                        if (ImGui.Selectable(jobName, string.Equals(_selectedJobEntry, job.Key.ToString(), StringComparison.Ordinal)))
                        {
                            _selectedJobEntry = job.Key.ToString();
                            _storedPathsToRemove.Clear();
                            _filePathResolve.Clear();
                            _filterFilePath = string.Empty;
                            _filterGamePath = string.Empty;
                        }
                    }
                }
            }
        }
        ImGui.SameLine();
        using (ImRaii.Group())
        {
            var selectedList = string.Equals(_selectedJobEntry, "alljobs", StringComparison.Ordinal)
                ? config[_selectedStoredCharacter].GlobalPersistentCache
                : (string.IsNullOrEmpty(_selectedJobEntry) ? [] : config[_selectedStoredCharacter].JobSpecificCache[uint.Parse(_selectedJobEntry)]);
            ImGui.TextUnformatted($"Attached Files (Total Files: {selectedList.Count})");
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(3);
            using (ImRaii.Disabled(string.IsNullOrEmpty(_selectedJobEntry)))
            {

                var restContent = availableContentRegion.X - ImGui.GetCursorPosX();
                using var group = ImRaii.Group();
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.ArrowRight, "Resolve Game Paths to used File Paths"))
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
                ImGuiHelpers.ScaledDummy(20, 1);
                ImGui.SameLine();
                using (ImRaii.Disabled(!_storedPathsToRemove.Any()))
                {
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, "Remove selected Game Paths"))
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
                using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
                {
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, "Clear ALL Game Paths"))
                    {
                        selectedList.Clear();
                        _transientConfigService.Save();
                        _transientResourceManager.RebuildSemiTransientResources();
                        _filterFilePath = string.Empty;
                        _filterGamePath = string.Empty;
                    }
                }
                UiSharedService.AttachToolTip("Hold CTRL to delete all game paths from the displayed list"
                    + UiSharedService.TooltipSeparator + "You usually do not need to do this. All animation and VFX data will be automatically handled through Sphene.");
                ImGuiHelpers.ScaledDummy(5);
                ImGuiHelpers.ScaledDummy(30);
                ImGui.SameLine();
                ImGui.SetNextItemWidth((restContent - 30) / 2f);
                ImGui.InputTextWithHint("##filterGamePath", "Filter by Game Path", ref _filterGamePath, 255);
                ImGui.SameLine();
                ImGui.SetNextItemWidth((restContent - 30) / 2f);
                ImGui.InputTextWithHint("##filterFilePath", "Filter by File Path", ref _filterFilePath, 255);

                using (var dataTable = ImRaii.Table("##table", 3, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg))
                {
                    if (dataTable)
                    {
                        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 30);
                        ImGui.TableSetupColumn("Game Path", ImGuiTableColumnFlags.WidthFixed, (restContent - 30) / 2f);
                        ImGui.TableSetupColumn("File Path", ImGuiTableColumnFlags.WidthFixed, (restContent - 30) / 2f);
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
                            if (ImGui.Checkbox("##", ref isSelected))
                            {
                                if (isSelected)
                                    _storedPathsToRemove.Add(entry);
                                else
                                    _storedPathsToRemove.Remove(entry);
                            }
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(entry);
                            UiSharedService.AttachToolTip(entry + UiSharedService.TooltipSeparator + "Click to copy to clipboard");
                            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                            {
                                ImGui.SetClipboardText(entry);
                            }
                            ImGui.TableNextColumn();
                            if (hasFileResolve)
                            {
                                ImGui.TextUnformatted(filePath ?? "Unk");
                                UiSharedService.AttachToolTip(filePath ?? "Unk" + UiSharedService.TooltipSeparator + "Click to copy to clipboard");
                                if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                                {
                                    ImGui.SetClipboardText(filePath);
                                }
                            }
                            else
                            {
                                ImGui.TextUnformatted("-");
                                UiSharedService.AttachToolTip("Resolve Game Paths to used File Paths to display the associated file paths.");
                            }
                        }
                    }
                }
            }
        }
    }

    private void DrawRecording()
    {
        UiSharedService.DrawTree("What is this? (Explanation / Help)", () =>
        {
            UiSharedService.TextWrapped("This tab allows you to attempt to fix mods that do not sync correctly, especially those with modded models and animations." + Environment.NewLine + Environment.NewLine
                + "To use this, start the recording, execute one or multiple emotes/animations you want to attempt to fix and check if new data appears in the table below." + Environment.NewLine
                + "If it doesn't, Sphene is not able to catch the data or already has recorded the animation files (check 'Show previously added transient files' to see if not all is already present)." + Environment.NewLine + Environment.NewLine
                + "For most animations, vfx, etc. it is enough to just run them once unless they have random variations. Longer animations do not require to play out in their entirety to be captured.");
            ImGuiHelpers.ScaledDummy(5);
            UiSharedService.DrawGroupedCenteredColorText("Important Note: If you need to fix an animation that should apply across multiple jobs, you need to repeat this process with at least one additional job, " +
                "otherwise the animation will only be fixed for the currently active job. This goes primarily for emotes that are used across multiple jobs.",
                ImGuiColors.DalamudYellow, 800);
            ImGuiHelpers.ScaledDummy(5);
            UiSharedService.DrawGroupedCenteredColorText("WARNING: WHILE RECORDING TRANSIENT DATA, DO NOT CHANGE YOUR APPEARANCE, ENABLED MODS OR ANYTHING. JUST DO THE ANIMATION(S) OR WHATEVER YOU NEED DOING AND STOP THE RECORDING.",
                ImGuiColors.DalamudRed, 800);
            ImGuiHelpers.ScaledDummy(5);
        });
        using (ImRaii.Disabled(_transientResourceManager.IsTransientRecording))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Play, "Start Transient Recording"))
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
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Stop, "Stop Transient Recording"))
            {
                _transientRecordCts.Cancel();
            }
        }
        if (_transientResourceManager.IsTransientRecording)
        {
            ImGui.SameLine();
            UiSharedService.ColorText($"RECORDING - Time Remaining: {_transientResourceManager.RecordTimeRemaining.Value}", ImGuiColors.DalamudYellow);
            ImGuiHelpers.ScaledDummy(5);
            UiSharedService.DrawGroupedCenteredColorText("DO NOT CHANGE YOUR APPEARANCE OR MODS WHILE RECORDING, YOU CAN ACCIDENTALLY MAKE SOME OF YOUR APPEARANCE RELATED MODS PERMANENT.", ImGuiColors.DalamudRed, 800);
        }

        ImGuiHelpers.ScaledDummy(5);
        ImGui.Checkbox("Show previously added transient files in the recording", ref _showAlreadyAddedTransients);
        _uiSharedService.DrawHelpText("Use this only if you want to see what was previously already caught by Sphene");
        ImGuiHelpers.ScaledDummy(5);

        using (ImRaii.Disabled(_transientResourceManager.IsTransientRecording || _transientResourceManager.RecordedTransients.All(k => !k.AddTransient) || !_acknowledgeReview))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Save, "Save Recorded Transient Data"))
            {
                _transientResourceManager.SaveRecording();
                _acknowledgeReview = false;
            }
        }
        ImGui.SameLine();
        ImGui.Checkbox("I acknowledge I have reviewed the recorded data", ref _acknowledgeReview);
        if (_transientResourceManager.RecordedTransients.Any(k => !k.AlreadyTransient))
        {
            ImGuiHelpers.ScaledDummy(5);
            UiSharedService.DrawGroupedCenteredColorText("Please review the recorded mod files before saving and deselect files that got into the recording on accident.", ImGuiColors.DalamudYellow);
            ImGuiHelpers.ScaledDummy(5);
        }

        ImGuiHelpers.ScaledDummy(5);
        var width = ImGui.GetContentRegionAvail();
        using var table = ImRaii.Table("Recorded Transients", 4, ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg);
        if (table)
        {
            int id = 0;
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 30);
            ImGui.TableSetupColumn("Owner", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("Game Path", ImGuiTableColumnFlags.WidthFixed, (width.X - 30 - 100) / 2f);
            ImGui.TableSetupColumn("File Path", ImGuiTableColumnFlags.WidthFixed, (width.X - 30 - 100) / 2f);
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
                if (ImGui.Checkbox("##add", ref addTransient))
                {
                    value.AddTransient = addTransient;
                }
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(value.Owner.Name);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(value.GamePath);
                UiSharedService.AttachToolTip(value.GamePath);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(value.FilePath);
                UiSharedService.AttachToolTip(value.FilePath);
                if (value.AlreadyTransient)
                {
                    ImGui.PopStyleColor();
                }
            }
        }
    }

    private void DrawAnalysis()
    {
        UiSharedService.DrawTree("What is this? (Explanation / Help)", () =>
        {
            UiSharedService.TextWrapped("This tab shows you all files and their sizes that are currently in use through your character and associated entities in Sphene");
        });

        if (_cachedAnalysis!.Count == 0) return;

        bool isAnalyzing = _characterAnalyzer.IsAnalysisRunning;
        if (isAnalyzing)
        {
            UiSharedService.ColorTextWrapped($"Analyzing {_characterAnalyzer.CurrentFile}/{_characterAnalyzer.TotalFiles}",
                ImGuiColors.DalamudYellow);
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.StopCircle, "Cancel analysis"))
            {
                _characterAnalyzer.CancelAnalyze();
            }
        }
        else
        {
            if (_cachedAnalysis!.Any(c => c.Value.Any(f => !f.Value.IsComputed)))
            {
                UiSharedService.ColorTextWrapped("Some entries in the analysis have file size not determined yet, press the button below to analyze your current data",
                    ImGuiColors.DalamudYellow);
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.PlayCircle, "Start analysis (missing entries)"))
                {
                    _ = _characterAnalyzer.ComputeAnalysis(print: false);
                }
            }
            else
            {
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.PlayCircle, "Start analysis (recalculate all entries)"))
                {
                    _ = _characterAnalyzer.ComputeAnalysis(print: false, recalculate: true);
                }
            }
        }

        ImGui.Separator();

        ImGui.TextUnformatted("Total files:");
        ImGui.SameLine();
        ImGui.TextUnformatted(_cachedAnalysis!.Values.Sum(c => c.Values.Count).ToString());
        ImGui.SameLine();
        using (var font = ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextUnformatted(FontAwesomeIcon.InfoCircle.ToIconString());
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
        ImGui.TextUnformatted("Total size (actual):");
        ImGui.SameLine();
        ImGui.TextUnformatted(UiSharedService.ByteToString(_cachedAnalysis!.Sum(c => c.Value.Sum(c => c.Value.OriginalSize))));
        ImGui.TextUnformatted("Total size (compressed for up/download only):");
        ImGui.SameLine();
        ImGui.TextUnformatted(UiSharedService.ByteToString(_cachedAnalysis!.Sum(c => c.Value.Sum(c => c.Value.CompressedSize))));
        ImGui.TextUnformatted($"Total modded model triangles: {_cachedAnalysis.Sum(c => c.Value.Sum(f => f.Value.Triangles))}");
        ImGui.Separator();
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

                ImGui.TextUnformatted("Files for " + kvp.Key);
                ImGui.SameLine();
                ImGui.TextUnformatted(kvp.Value.Count.ToString());
                ImGui.SameLine();

                using (var font = ImRaii.PushFont(UiBuilder.IconFont))
                {
                    ImGui.TextUnformatted(FontAwesomeIcon.InfoCircle.ToIconString());
                }
                if (ImGui.IsItemHovered())
                {
                    string text = "";
                    text = string.Join(Environment.NewLine, groupedfiles
                        .Select(f => f.Key + ": " + f.Count() + " files, size: " + UiSharedService.ByteToString(f.Sum(v => v.OriginalSize))
                        + ", compressed: " + UiSharedService.ByteToString(f.Sum(v => v.CompressedSize))));
                    ImGui.SetTooltip(text);
                }
                ImGui.TextUnformatted($"{kvp.Key} size (actual):");
                ImGui.SameLine();
                ImGui.TextUnformatted(UiSharedService.ByteToString(kvp.Value.Sum(c => c.Value.OriginalSize)));
                ImGui.TextUnformatted($"{kvp.Key} size (compressed for up/download only):");
                ImGui.SameLine();
                ImGui.TextUnformatted(UiSharedService.ByteToString(kvp.Value.Sum(c => c.Value.CompressedSize)));
                ImGui.Separator();

                var vramUsage = groupedfiles.SingleOrDefault(v => string.Equals(v.Key, "tex", StringComparison.Ordinal));
                if (vramUsage != null)
                {
                    var actualVramUsage = vramUsage.Sum(f => f.OriginalSize);
                    ImGui.TextUnformatted($"{kvp.Key} VRAM usage:");
                    ImGui.SameLine();
                    ImGui.TextUnformatted(UiSharedService.ByteToString(actualVramUsage));
                    if (_playerPerformanceConfig.Current.WarnOnExceedingThresholds
                        || _playerPerformanceConfig.Current.ShowPerformanceIndicator)
                    {
                        using var _ = ImRaii.PushIndent(10f);
                        var currentVramWarning = _playerPerformanceConfig.Current.VRAMSizeWarningThresholdMiB;
                        ImGui.TextUnformatted($"Configured VRAM warning threshold: {currentVramWarning} MiB.");
                        if (currentVramWarning * 1024 * 1024 < actualVramUsage)
                        {
                            UiSharedService.ColorText($"You exceed your own threshold by " +
                                $"{UiSharedService.ByteToString(actualVramUsage - (currentVramWarning * 1024 * 1024))}.",
                                ImGuiColors.DalamudYellow);
                        }
                    }
                }

                var actualTriCount = kvp.Value.Sum(f => f.Value.Triangles);
                ImGui.TextUnformatted($"{kvp.Key} modded model triangles: {actualTriCount}");
                if (_playerPerformanceConfig.Current.WarnOnExceedingThresholds
                    || _playerPerformanceConfig.Current.ShowPerformanceIndicator)
                {
                    using var _ = ImRaii.PushIndent(10f);
                    var currentTriWarning = _playerPerformanceConfig.Current.TrisWarningThresholdThousands;
                    ImGui.TextUnformatted($"Configured triangle warning threshold: {currentTriWarning * 1000} triangles.");
                    if (currentTriWarning * 1000 < actualTriCount)
                    {
                        UiSharedService.ColorText($"You exceed your own threshold by " +
                            $"{actualTriCount - (currentTriWarning * 1000)} triangles.",
                            ImGuiColors.DalamudYellow);
                    }
                }

                ImGui.Separator();
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
                    using var tabcol = ImRaii.PushColor(ImGuiCol.Tab, UiSharedService.Color(ImGuiColors.DalamudYellow), requiresCompute);
                    if (requiresCompute)
                    {
                        fileGroupText += " (!)";
                    }
                    ImRaii.IEndObject fileTab;
                    using (var textcol = ImRaii.PushColor(ImGuiCol.Text, UiSharedService.Color(new(0, 0, 0, 1)),
                        requiresCompute && !string.Equals(_selectedFileTypeTab, fileGroup.Key, StringComparison.Ordinal)))
                    {
                        fileTab = ImRaii.TabItem(fileGroupText + "###" + fileGroup.Key);
                    }

                    if (!fileTab) { fileTab.Dispose(); continue; }

                    if (!string.Equals(fileGroup.Key, _selectedFileTypeTab, StringComparison.Ordinal))
                    {
                        _selectedFileTypeTab = fileGroup.Key;
                        _selectedHash = string.Empty;
                        _texturesToConvert.Clear();
                    }

                    ImGui.TextUnformatted($"{fileGroup.Key} files");
                    ImGui.SameLine();
                    ImGui.TextUnformatted(fileGroup.Count().ToString());

                    ImGui.TextUnformatted($"{fileGroup.Key} files size (actual):");
                    ImGui.SameLine();
                    ImGui.TextUnformatted(UiSharedService.ByteToString(fileGroup.Sum(c => c.OriginalSize)));

                    ImGui.TextUnformatted($"{fileGroup.Key} files size (compressed for up/download only):");
                    ImGui.SameLine();
                    ImGui.TextUnformatted(UiSharedService.ByteToString(fileGroup.Sum(c => c.CompressedSize)));

                    if (string.Equals(_selectedFileTypeTab, "tex", StringComparison.Ordinal))
                    {
                        ImGui.Checkbox("Enable BC7 Conversion Mode", ref _enableBc7ConversionMode);
                        if (_enableBc7ConversionMode)
                        {
                            ImGui.Checkbox("Create backup before conversion", ref _enableBackupBeforeConversion);
                            UiSharedService.AttachToolTip("Stores original textures prior to conversion. Smaller storage footprint than full-mod PMP but only per-file restore.");
                            if (_enableBackupBeforeConversion)
                            {
                                UiSharedService.ColorTextWrapped("Backups will be created in the mod folder under 'sphene_backups' before conversion.", ImGuiColors.ParsedGreen);
                                UiSharedService.ColorTextWrapped("Using full-mod PMP backups increases required storage, but enables safer complete mod restoration.", ImGuiColors.DalamudYellow);
                            }
                            
                            // Revert functionality
            ImGui.Separator();
            ImGui.TextUnformatted("Backup Management:");
            
            var availableBackups = GetCachedBackupsForCurrentAnalysis();
            if (availableBackups.Count > 0)
            {
                UiSharedService.ColorTextWrapped($"Found {availableBackups.Count} backed up texture(s) from current analysis that can be restored.", ImGuiColors.ParsedGreen);
                
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Undo, $"Revert current textures ({availableBackups.Count})"))
                {
                    StartTextureRevert(availableBackups);
                }
                                
                                ImGui.SameLine();
                                if (_uiSharedService.IconTextButton(FontAwesomeIcon.FolderOpen, "Open backup folder"))
                                {
                                    try
                                    {
                                        var overview = _shrinkuBackupService.GetBackupOverviewAsync().GetAwaiter().GetResult();
                                        var firstPath = overview.FirstOrDefault()?.SourcePath;
                                        var backupDirectory = string.IsNullOrEmpty(firstPath) ? _textureBackupService.GetBackupDirectory() : Path.GetDirectoryName(firstPath)!;
                                        if (!string.IsNullOrEmpty(backupDirectory) && Directory.Exists(backupDirectory))
                                        {
                                            System.Diagnostics.Process.Start("explorer.exe", backupDirectory);
                                        }
                                    }
                                    catch
                                    {
                                        var fallback = _textureBackupService.GetBackupDirectory();
                                        if (Directory.Exists(fallback))
                                            System.Diagnostics.Process.Start("explorer.exe", fallback);
                                    }
                                }
                            }
                            else
                            {
                                UiSharedService.ColorTextWrapped("No texture backups found.", ImGuiColors.DalamudGrey);
                            }
                            
                            // Storage information and cleanup
                            ImGui.Separator();
                            ImGui.TextUnformatted("Storage Management:");
                            
                            // Update storage info every 5 seconds or if task is null
                            var now = DateTime.UtcNow;
                            if (_storageInfoTask == null || (((now - _lastStorageInfoUpdate).TotalSeconds > 5) && _storageInfoTask.IsCompleted))
                            {
                                _storageInfoTask = GetShrinkUStorageInfoAsync();
                                _lastStorageInfoUpdate = now;
                            }
                            
                            // Display storage info
                            if (_storageInfoTask != null && _storageInfoTask.IsCompleted)
                            {
                                _cachedStorageInfo = _storageInfoTask.Result;
                                var (totalSize, fileCount) = _cachedStorageInfo;
                                
                                if (fileCount > 0)
                                {
                                    var sizeInMB = totalSize / (1024.0 * 1024.0);
                                    UiSharedService.ColorTextWrapped($"Total backup storage: {sizeInMB:F2} MB ({fileCount} files)", ImGuiColors.DalamudYellow);
                                    
                                    // Time-based backup cleanup removed
                                }
                                else
                                {
                                    UiSharedService.ColorTextWrapped("No backup files found.", ImGuiColors.DalamudGrey);
                                }
                            }
                            else
                            {
                                // Show cached info if available, otherwise show loading
                                if (_cachedStorageInfo.fileCount > 0)
                                {
                                    var sizeInMB = _cachedStorageInfo.totalSize / (1024.0 * 1024.0);
                                    UiSharedService.ColorTextWrapped($"Total backup storage: {sizeInMB:F2} MB ({_cachedStorageInfo.fileCount} files) [Updating...]", ImGuiColors.DalamudGrey);
                                }
                                else
                                {
                                    UiSharedService.ColorTextWrapped("Calculating storage usage...", ImGuiColors.DalamudGrey);
                                }
                            }
                            
                            UiSharedService.ColorText("BC7 CONVERSION INFO:", ImGuiColors.DalamudYellow);
                            ImGui.SameLine();
                            UiSharedService.ColorText("Backups are created automatically for revert functionality!", ImGuiColors.ParsedGreen);
                            UiSharedService.ColorTextWrapped("- Converting textures to BC7 will reduce their size (compressed and uncompressed) drastically. It is recommended to be used for large (4k+) textures." +
                            Environment.NewLine + "- Some textures, especially ones utilizing colorsets, might not be suited for BC7 conversion and might produce visual artifacts." +
                            Environment.NewLine + "- Original textures are automatically backed up before conversion and can be restored using the 'Revert current textures' button above." +
                            Environment.NewLine + "- Conversion will convert all found texture duplicates (entries with more than 1 file path) automatically." +
                            Environment.NewLine + "- Converting textures to BC7 is a very expensive operation and, depending on the amount of textures to convert, will take a while to complete."
                                , ImGuiColors.DalamudYellow);
                            
                            // One-click conversion button
                            var nonBc7Textures = GetNonBc7Textures(fileGroup);
                            if (nonBc7Textures.Count > 0 && _uiSharedService.IconTextButton(FontAwesomeIcon.Compress, $"Convert all {nonBc7Textures.Count} non-BC7 textures"))
                            {
                                _logger.LogDebug("One-click conversion button clicked. Found {Count} non-BC7 textures", nonBc7Textures.Count);
                                // Add all non-BC7 textures to conversion list without clearing existing selections
                                foreach (var texture in nonBc7Textures)
                                {
                                    // Use the first file path as the primary texture to convert
                                    var primaryPath = texture.FilePaths[0];
                                    var duplicatePaths = texture.FilePaths.Skip(1).ToArray();
                                    _texturesToConvert[primaryPath] = duplicatePaths;
                                    _logger.LogDebug("Added texture {PrimaryPath} with {DuplicateCount} duplicates to conversion list", primaryPath, duplicatePaths.Length);
                                }
                                _logger.LogDebug("Total textures in conversion list: {Count}", _texturesToConvert.Count);
                            }
                            
                            if (_texturesToConvert.Count > 0 && _uiSharedService.IconTextButton(FontAwesomeIcon.PlayCircle, "Start conversion of " + _texturesToConvert.Count + " texture(s)"))
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

                    ImGui.Separator();
                    DrawTable(fileGroup);

                    fileTab.Dispose();
                }
            }
        }

        ImGui.Separator();

        ImGui.TextUnformatted("Selected file:");
        ImGui.SameLine();
        UiSharedService.ColorText(_selectedHash, ImGuiColors.DalamudYellow);

        if (_cachedAnalysis[_selectedObjectTab].TryGetValue(_selectedHash, out CharacterAnalyzer.FileDataEntry? item))
        {
            var filePaths = item.FilePaths;
            ImGui.TextUnformatted("Local file path:");
            ImGui.SameLine();
            UiSharedService.TextWrapped(filePaths[0]);
            if (filePaths.Count > 1)
            {
                ImGui.SameLine();
                ImGui.TextUnformatted($"(and {filePaths.Count - 1} more)");
                ImGui.SameLine();
                _uiSharedService.IconText(FontAwesomeIcon.InfoCircle);
                UiSharedService.AttachToolTip(string.Join(Environment.NewLine, filePaths.Skip(1)));
            }

            var gamepaths = item.GamePaths;
            ImGui.TextUnformatted("Used by game path:");
            ImGui.SameLine();
            UiSharedService.TextWrapped(gamepaths[0]);
            if (gamepaths.Count > 1)
            {
                ImGui.SameLine();
                ImGui.TextUnformatted($"(and {gamepaths.Count - 1} more)");
                ImGui.SameLine();
                _uiSharedService.IconText(FontAwesomeIcon.InfoCircle);
                UiSharedService.AttachToolTip(string.Join(Environment.NewLine, gamepaths.Skip(1)));
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
        var modDisplayNames = GetModDisplayNamesSnapshot();
        var tableColumns = string.Equals(fileGroup.Key, "tex", StringComparison.Ordinal)
            ? (_enableBc7ConversionMode ? 8 : 7)
            : (string.Equals(fileGroup.Key, "mdl", StringComparison.Ordinal) ? 7 : 6);
        using var table = ImRaii.Table("Analysis", tableColumns, ImGuiTableFlags.Sortable | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit,
            new Vector2(0, 300));
        if (!table.Success) return;
        ImGui.TableSetupColumn("Hash");
        ImGui.TableSetupColumn("Filepaths");
        ImGui.TableSetupColumn("Gamepaths");
        ImGui.TableSetupColumn("Original Size");
        ImGui.TableSetupColumn("Compressed Size");
        ImGui.TableSetupColumn("Mod Name");
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
            if (idx == 5 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Ascending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => GetModDisplayName(modDisplayNames, k.Value.ModName), StringComparer.OrdinalIgnoreCase).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (idx == 5 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Descending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => GetModDisplayName(modDisplayNames, k.Value.ModName), StringComparer.OrdinalIgnoreCase).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (string.Equals(fileGroup.Key, "mdl", StringComparison.Ordinal) && idx == 6 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Ascending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Value.Triangles).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (string.Equals(fileGroup.Key, "mdl", StringComparison.Ordinal) && idx == 6 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Descending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => k.Value.Triangles).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (string.Equals(fileGroup.Key, "tex", StringComparison.Ordinal) && idx == 6 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Ascending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Value.Format.Value, StringComparer.Ordinal).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (string.Equals(fileGroup.Key, "tex", StringComparison.Ordinal) && idx == 6 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Descending)
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
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, UiSharedService.Color(ImGuiColors.DalamudRed));
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, UiSharedService.Color(ImGuiColors.DalamudRed));
            }
            if (string.Equals(_selectedHash, item.Hash, StringComparison.Ordinal))
            {
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, UiSharedService.Color(ImGuiColors.DalamudYellow));
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, UiSharedService.Color(ImGuiColors.DalamudYellow));
            }
            ImGui.TextUnformatted(item.Hash);
            if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.FilePaths.Count.ToString());
            if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.GamePaths.Count.ToString());
            if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(UiSharedService.ByteToString(item.OriginalSize));
            if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(UiSharedService.ByteToString(item.CompressedSize));
            if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(GetModDisplayName(modDisplayNames, item.ModName));
            if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
            if (string.Equals(fileGroup.Key, "tex", StringComparison.Ordinal))
            {
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.Format.Value);
                if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
                if (_enableBc7ConversionMode)
                {
                    ImGui.TableNextColumn();
                    if (string.Equals(item.Format.Value, "BC7", StringComparison.Ordinal))
                    {
                        ImGui.TextUnformatted("");
                        continue;
                    }
                    var filePath = item.FilePaths[0];
                    bool toConvert = _texturesToConvert.ContainsKey(filePath);
                    if (ImGui.Checkbox("###convert" + item.Hash, ref toConvert))
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
                ImGui.TextUnformatted(item.Triangles.ToString());
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
