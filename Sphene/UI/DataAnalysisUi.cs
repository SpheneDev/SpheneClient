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

namespace Sphene.UI;

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
    private readonly Dictionary<string, string[]> _texturesToConvert = new(StringComparer.Ordinal);
    private Dictionary<ObjectKind, Dictionary<string, CharacterAnalyzer.FileDataEntry>>? _cachedAnalysis;
    private CancellationTokenSource _conversionCancellationTokenSource = new();
    private string _conversionCurrentFileName = string.Empty;
    private int _conversionCurrentFileProgress = 0;
    private Task? _conversionTask;
    private bool _enableBc7ConversionMode = true;
    private bool _enableBackupBeforeConversion = true;
    private bool _autoConvertNonBc7 = false;
    private bool _autoConvertTriggered = false;
    private bool _hasUpdate = false;
    private bool _modalOpen = false;
    private Task? _backupTask;
    private readonly Progress<(string, int, int)> _backupProgress = new();
    private Task? _revertTask;
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

    public DataAnalysisUi(ILogger<DataAnalysisUi> logger, SpheneMediator mediator,
        CharacterAnalyzer characterAnalyzer, IpcManager ipcManager,
        PerformanceCollectorService performanceCollectorService, UiSharedService uiSharedService,
        PlayerPerformanceConfigService playerPerformanceConfig, TransientResourceManager transientResourceManager,
        TransientConfigService transientConfigService, TextureBackupService textureBackupService)
        : base(logger, mediator, "Sphene Character Data Analysis", performanceCollectorService)
    {
        _characterAnalyzer = characterAnalyzer;
        _ipcManager = ipcManager;
        _uiSharedService = uiSharedService;
        _playerPerformanceConfig = playerPerformanceConfig;
        _transientResourceManager = transientResourceManager;
        _transientConfigService = transientConfigService;
        _textureBackupService = textureBackupService;
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
                ImGui.TextUnformatted("BC7 Conversion in progress: " + _conversionCurrentFileProgress + "/" + _texturesToConvert.Count);
                UiSharedService.TextWrapped("Current file: " + _conversionCurrentFileName);
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.StopCircle, "Cancel conversion"))
                {
                    _conversionCancellationTokenSource.Cancel();
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
        else if (_conversionTask != null && _conversionTask.IsCompleted && _texturesToConvert.Count > 0)
        {
            _conversionTask = null;
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
    }

    private bool _showAlreadyAddedTransients = false;
    private bool _acknowledgeReview = false;
    private string _selectedStoredCharacter = string.Empty;
    private string _selectedJobEntry = string.Empty;
    private readonly List<string> _storedPathsToRemove = [];
    private readonly Dictionary<string, string> _filePathResolve = [];
    private string _filterGamePath = string.Empty;
    private string _filterFilePath = string.Empty;

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
        using (ImRaii.Group())
        {
            ImGui.TextUnformatted("Character");
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(3);
            availableContentRegion = ImGui.GetContentRegionAvail();
            using (ImRaii.ListBox("##characters", new Vector2(200, availableContentRegion.Y)))
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
            using (ImRaii.ListBox("##data", new Vector2(150, availableContentRegion.Y)))
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
                            if (_enableBackupBeforeConversion)
                            {
                                UiSharedService.ColorTextWrapped("Backups will be created in the mod folder under 'sphene_backups' before conversion.", ImGuiColors.ParsedGreen);
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
                                    var backupDirectory = _textureBackupService.GetBackupDirectory();
                                    if (Directory.Exists(backupDirectory))
                                    {
                                        System.Diagnostics.Process.Start("explorer.exe", backupDirectory);
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
                            var now = DateTime.Now;
                            if (_storageInfoTask == null || (now - _lastStorageInfoUpdate).TotalSeconds > 5)
                            {
                                if (_storageInfoTask == null || _storageInfoTask.IsCompleted)
                                {
                                    _storageInfoTask = _textureBackupService.GetBackupStorageInfoAsync();
                                    _lastStorageInfoUpdate = now;
                                }
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
                                    
                                    ImGui.SameLine();
                                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Broom, "Cleanup old backups (3+ days)"))
                                    {
                                        Task.Run(async () =>
                                        {
                                            var (deletedCount, freedSpace) = await _textureBackupService.CleanupOldBackupsAsync(3);
                                            var freedMB = freedSpace / (1024.0 * 1024.0);
                                            _logger.LogInformation("Backup cleanup completed: {deletedCount} files deleted, {freedMB:F2} MB freed", deletedCount, freedMB);
                                            
                                            // Refresh storage info after cleanup
                                            _storageInfoTask = null;
                                        });
                                    }
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

                            // Auto-convert toggle for non-BC7 textures
                            bool autoConvert = _autoConvertNonBc7;
                            if (ImGui.Checkbox("Auto-convert non-BC7 textures", ref autoConvert))
                            {
                                _autoConvertNonBc7 = autoConvert;
                                if (!_autoConvertNonBc7)
                                    _autoConvertTriggered = false;
                            }
                            
                            // One-click conversion button
                            var nonBc7Textures = GetNonBc7Textures(fileGroup);

                            // Automatically trigger conversion once if enabled and conditions are met
                            if (_autoConvertNonBc7 && !_autoConvertTriggered && nonBc7Textures.Count > 0 && (_conversionTask == null || _conversionTask.IsCompleted) && (_backupTask == null || _backupTask.IsCompleted))
                            {
                                _logger.LogDebug("Auto-convert enabled. Preparing {Count} non-BC7 textures for conversion.", nonBc7Textures.Count);
                                foreach (var texture in nonBc7Textures)
                                {
                                    var primaryPath = texture.FilePaths.First();
                                    var duplicatePaths = texture.FilePaths.Skip(1).ToArray();
                                    _texturesToConvert[primaryPath] = duplicatePaths;
                                }

                                if (_texturesToConvert.Count > 0)
                                {
                                    _logger.LogDebug("Starting auto-conversion of {Count} textures.", _texturesToConvert.Count);
                                    if (_enableBackupBeforeConversion)
                                    {
                                        StartBackupAndConversion();
                                    }
                                    else
                                    {
                                        _conversionCancellationTokenSource = _conversionCancellationTokenSource.CancelRecreate();
                                        _conversionTask = _ipcManager.Penumbra.ConvertTextureFiles(_logger, _texturesToConvert, _conversionProgress, _conversionCancellationTokenSource.Token);
                                    }
                                    _autoConvertTriggered = true;
                                }
                            }
                            if (nonBc7Textures.Count > 0 && _uiSharedService.IconTextButton(FontAwesomeIcon.Compress, $"Convert all {nonBc7Textures.Count} non-BC7 textures"))
                            {
                                _logger.LogDebug("One-click conversion button clicked. Found {Count} non-BC7 textures", nonBc7Textures.Count);
                                // Add all non-BC7 textures to conversion list without clearing existing selections
                                foreach (var texture in nonBc7Textures)
                                {
                                    // Use the first file path as the primary texture to convert
                                    var primaryPath = texture.FilePaths.First();
                                    var duplicatePaths = texture.FilePaths.Skip(1).ToArray();
                                    _texturesToConvert[primaryPath] = duplicatePaths;
                                    _logger.LogDebug("Added texture {PrimaryPath} with {DuplicateCount} duplicates to conversion list", primaryPath, duplicatePaths.Length);
                                }
                                _logger.LogDebug("Total textures in conversion list: {Count}", _texturesToConvert.Count);
                            }
                            
                            if (_texturesToConvert.Count > 0 && _uiSharedService.IconTextButton(FontAwesomeIcon.PlayCircle, "Start conversion of " + _texturesToConvert.Count + " texture(s)"))
                            {
                                if (_enableBackupBeforeConversion)
                                {
                                    StartBackupAndConversion();
                                }
                                else
                                {
                                    _conversionCancellationTokenSource = _conversionCancellationTokenSource.CancelRecreate();
                                    _conversionTask = _ipcManager.Penumbra.ConvertTextureFiles(_logger, _texturesToConvert, _conversionProgress, _conversionCancellationTokenSource.Token);
                                }
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
    }

    private void ConversionProgress_ProgressChanged(object? sender, (string, int) e)
    {
        _conversionCurrentFileName = e.Item1;
        _conversionCurrentFileProgress = e.Item2;
    }

    private void DrawTable(IGrouping<string, CharacterAnalyzer.FileDataEntry> fileGroup)
    {
        var tableColumns = string.Equals(fileGroup.Key, "tex", StringComparison.Ordinal)
            ? (_enableBc7ConversionMode ? 8 : 7)
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
            ImGui.TableSetupColumn("Resolution");
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
            // Sorting for textures: Resolution at index 5, Format at index 6
            if (string.Equals(fileGroup.Key, "tex", StringComparison.Ordinal) && idx == 5 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Ascending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Value.Resolution.Value.Width * k.Value.Resolution.Value.Height).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (string.Equals(fileGroup.Key, "tex", StringComparison.Ordinal) && idx == 5 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Descending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => k.Value.Resolution.Value.Width * k.Value.Resolution.Value.Height).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
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
            if (string.Equals(fileGroup.Key, "tex", StringComparison.Ordinal))
            {
                ImGui.TableNextColumn();
                var res = item.Resolution.Value;
                ImGui.TextUnformatted(res.Width > 0 && res.Height > 0 ? $"{res.Width}x{res.Height}" : "-");
                if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
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

    private List<CharacterAnalyzer.FileDataEntry> GetNonBc7Textures(IGrouping<string, CharacterAnalyzer.FileDataEntry> fileGroup)
    {
        return fileGroup.Where(item => !string.Equals(item.Format.Value, "BC7", StringComparison.Ordinal)).ToList();
    }

    private void StartBackupAndConversion()
    {
        _conversionCancellationTokenSource = _conversionCancellationTokenSource.CancelRecreate();
        
        _backupTask = Task.Run(async () =>
        {
            try
            {
                // Create backups first - backup primary paths and all duplicates
                var allTexturePaths = _texturesToConvert.SelectMany(kvp => new[] { kvp.Key }.Concat(kvp.Value)).ToArray();
                _logger.LogDebug("Starting backup for {Count} texture paths", allTexturePaths.Length);
                await _textureBackupService.BackupTexturesAsync(allTexturePaths, _backupProgress, _conversionCancellationTokenSource.Token);
                
                // Start conversion after backup is complete
                _logger.LogDebug("Backup completed, starting texture conversion");
                _conversionTask = _ipcManager.Penumbra.ConvertTextureFiles(_logger, _texturesToConvert, _conversionProgress, _conversionCancellationTokenSource.Token);
                
                try
                {
                    await _conversionTask;
                    _logger.LogDebug("Texture conversion completed successfully");
                }
                catch (Exception conversionEx)
                {
                    _logger.LogError(conversionEx, "Error during texture conversion: {ErrorMessage}", conversionEx.Message);
                    _logger.LogDebug("Full exception details: {Exception}", conversionEx.ToString());
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during backup process");
            }
        }, _conversionCancellationTokenSource.Token);
    }

    private void StartTextureRevert(Dictionary<string, List<string>> availableBackups)
    {
        _revertCancellationTokenSource = _revertCancellationTokenSource.CancelRecreate();
        
        _revertTask = Task.Run(async () =>
        {
            try
            {
                // Create mapping of backup files to their target locations
                var backupToTargetMap = new Dictionary<string, string>();
                
                foreach (var kvp in availableBackups)
                {
                    var originalFileName = kvp.Key;
                    var backupFiles = kvp.Value;
                    
                    // Use the most recent backup (first in the list since they're ordered by creation time)
                    if (backupFiles.Count > 0)
                    {
                        var mostRecentBackup = backupFiles.First();
                        
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
                    var results = await _textureBackupService.RestoreTexturesAsync(backupToTargetMap, deleteBackupsAfterRestore: true, _revertProgress, _revertCancellationTokenSource.Token);
                    
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
            DateTime.Now - _lastBackupAnalysisUpdate < TimeSpan.FromSeconds(5))
        {
            return _cachedBackupsForAnalysis;
        }

        _cachedBackupsForAnalysis = GetBackupsForCurrentAnalysis();
        _lastBackupAnalysisUpdate = DateTime.Now;
        return _cachedBackupsForAnalysis;
    }

    private Dictionary<string, List<string>> GetBackupsForCurrentAnalysis()
    {
        try
        {
            if (_cachedAnalysis == null) return new Dictionary<string, List<string>>();
            
            // Get all available backups
            var allBackups = _textureBackupService.GetBackupsByOriginalFile();
            var filteredBackups = new Dictionary<string, List<string>>();
            
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
            return new Dictionary<string, List<string>>();
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
}
