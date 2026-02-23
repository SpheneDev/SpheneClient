using Lumina.Data.Files;
using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.FileCache;
using Sphene.Services.Mediator;
using Sphene.UI;
using Sphene.Utils;
using Microsoft.Extensions.Logging;

namespace Sphene.Services;

public sealed class CharacterAnalyzer : MediatorSubscriberBase, IDisposable
{
    private readonly FileCacheManager _fileCacheManager;
    private readonly XivDataAnalyzer _xivDataAnalyzer;
    private readonly System.Threading.Lock _analysisLock = new();
    private CancellationTokenSource? _analysisCts;
    private CancellationTokenSource _baseAnalysisCts = new();
    private string _lastDataHash = string.Empty;

    public CharacterAnalyzer(ILogger<CharacterAnalyzer> logger, SpheneMediator mediator, FileCacheManager fileCacheManager, XivDataAnalyzer modelAnalyzer)
        : base(logger, mediator)
    {
        Mediator.Subscribe<CharacterDataCreatedMessage>(this, (msg) =>
        {
            _baseAnalysisCts.Cancel();
            _baseAnalysisCts.Dispose();
            _baseAnalysisCts = new();
            var token = _baseAnalysisCts.Token;
            _ = BaseAnalysis(msg.CharacterData, token);
        });
        _fileCacheManager = fileCacheManager;
        _xivDataAnalyzer = modelAnalyzer;
    }

    public int CurrentFile { get; internal set; }
    public bool IsAnalysisRunning => _analysisCts != null;
    public int TotalFiles { get; internal set; }
    public bool AreActiveTexturesComputed
    {
        get
        {
            lock (_analysisLock)
            {
                return LastAnalysis.Values
                    .SelectMany(v => v.Values)
                    .Where(f => f.IsActive && string.Equals(f.FileType, "tex", StringComparison.OrdinalIgnoreCase))
                    .All(f => f.IsComputed);
            }
        }
    }

    internal Dictionary<ObjectKind, Dictionary<string, FileDataEntry>> LastAnalysis { get; } = [];

    /// <summary>
    /// Gets the total size of active texture files in the latest analysis.
    /// </summary>
    public long GetActiveTextureSizeBytes()
    {
        lock (_analysisLock)
        {
            long totalSize = 0;
            foreach (var objectKindData in LastAnalysis.Values)
            {
                foreach (var fileEntry in objectKindData.Values)
                {
                    if (fileEntry.IsActive && string.Equals(fileEntry.FileType, "tex", StringComparison.OrdinalIgnoreCase))
                    {
                        totalSize += fileEntry.OriginalSize;
                    }
                }
            }

            return totalSize;
        }
    }

    public void CancelAnalyze()
    {
        _analysisCts?.CancelDispose();
        _analysisCts = null;
    }

    public async Task ComputeAnalysis(bool print = true, bool recalculate = false)
    {
        Logger.LogDebug("=== Calculating Character Analysis ===");

        _analysisCts = _analysisCts?.CancelRecreate() ?? new();

        var cancelToken = _analysisCts.Token;

        List<FileDataEntry> allFiles;
        lock (_analysisLock)
        {
            allFiles = LastAnalysis.SelectMany(v => v.Value.Select(d => d.Value)).ToList();
        }
        if (allFiles.Exists(c => !c.IsComputed || recalculate))
        {
            var remaining = allFiles.Where(c => !c.IsComputed || recalculate).ToList();
            var activeTexFiles = remaining.Where(c => c.IsActive && string.Equals(c.FileType, "tex", StringComparison.OrdinalIgnoreCase)).ToList();
            var otherFiles = remaining.Except(activeTexFiles).ToList();

            TotalFiles = remaining.Count;
            CurrentFile = 1;
            Logger.LogDebug("=== Computing {amount} remaining files ===", remaining.Count);

            Mediator.Publish(new HaltScanMessage(nameof(CharacterAnalyzer)));
            try
            {
                foreach (var file in activeTexFiles)
                {
                    Logger.LogDebug("Computing file {file}", file.FilePaths[0]);
                    await file.ComputeSizes(_fileCacheManager, cancelToken).ConfigureAwait(false);
                    CurrentFile++;
                }

                if (activeTexFiles.Count > 0)
                {
                    Mediator.Publish(new CharacterDataAnalyzedMessage());
                }

                foreach (var file in otherFiles)
                {
                    Logger.LogDebug("Computing file {file}", file.FilePaths[0]);
                    await file.ComputeSizes(_fileCacheManager, cancelToken).ConfigureAwait(false);
                    CurrentFile++;
                }

                _fileCacheManager.WriteOutFullCsv();

            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to analyze files");
            }
            finally
            {
                Mediator.Publish(new ResumeScanMessage(nameof(CharacterAnalyzer)));
            }
        }

        Mediator.Publish(new CharacterDataAnalyzedMessage());

        _analysisCts.CancelDispose();
        _analysisCts = null;

        if (print) PrintAnalysis();
    }

    public void Dispose()
    {
        _analysisCts?.Cancel();
        _analysisCts?.Dispose();
        _baseAnalysisCts?.Cancel();
        _baseAnalysisCts?.Dispose();
    }

    private async Task BaseAnalysis(CharacterData charaData, CancellationToken token)
    {
        if (string.Equals(charaData.DataHash.Value, _lastDataHash, StringComparison.Ordinal)) return;

        lock (_analysisLock)
        {
            LastAnalysis.Clear();
        }

        HashSet<string> relevantMinionMods = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> relevantMinionOptions = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> gamePathToModName = new(StringComparer.OrdinalIgnoreCase);

        foreach (var list in charaData.FileReplacements.Values)
        {
            foreach (var f in list)
            {
                if (string.IsNullOrEmpty(f.ModName)) continue;

                foreach (var gamePath in f.GamePaths)
                {
                    if (string.IsNullOrWhiteSpace(gamePath)) continue;
                    gamePathToModName[gamePath] = f.ModName;
                }

                bool isMinionPath = f.GamePaths.Any(p =>
                    p.StartsWith("chara/companion/", StringComparison.OrdinalIgnoreCase) ||
                    p.StartsWith("chara/monster/", StringComparison.OrdinalIgnoreCase) ||
                    p.StartsWith("chara/demihuman/", StringComparison.OrdinalIgnoreCase) ||
                    p.StartsWith("sound/voice/mon_", StringComparison.OrdinalIgnoreCase) ||
                    p.StartsWith("vfx/monster/", StringComparison.OrdinalIgnoreCase) ||
                    p.StartsWith("vfx/demihuman/", StringComparison.OrdinalIgnoreCase));

                if (isMinionPath)
                {
                    relevantMinionMods.Add(f.ModName);
                    if (!string.IsNullOrEmpty(f.OptionName))
                    {
                        relevantMinionOptions.Add($"{f.ModName}|{f.OptionName}");
                    }
                }
            }
        }

        Logger.LogDebug("Identified {count} relevant Minion Mods: {mods}", relevantMinionMods.Count, string.Join(", ", relevantMinionMods));
        Logger.LogDebug("Identified {count} relevant Minion Options: {options}", relevantMinionOptions.Count, string.Join(", ", relevantMinionOptions));

        // Identify SCDs currently assigned to Player that belong to Minion Options OR Minion Mods
        // These need to be MOVED from Player to MinionOrMount
        List<FileReplacementData> displacedScds = [];
        if (charaData.FileReplacements.TryGetValue(ObjectKind.Player, out var playerFiles))
        {
             foreach (var f in playerFiles)
             {
                 var isScd = f.GamePaths.Any(p => p.EndsWith(".scd", StringComparison.OrdinalIgnoreCase));
                 if (isScd)
                 {
                      string? effectiveModName = f.ModName;
                      string? effectiveOptionName = f.OptionName;
                      
                      // Check based on existing ModName
                      bool isRelevantMod = !string.IsNullOrEmpty(effectiveModName) && relevantMinionMods.Contains(effectiveModName);
                      bool isRelevantOption = !string.IsNullOrEmpty(effectiveModName) && relevantMinionOptions.Contains($"{effectiveModName}|{effectiveOptionName}");

                      // If not found relevant yet, try to find a better ModName via GamePaths
                      // This handles cases where ModName in FileReplacement might be missing or different (e.g. Directory vs Meta Name)
                      if (!isRelevantMod && !isRelevantOption)
                      {
                          foreach (var gp in f.GamePaths)
                          {
                             if (gamePathToModName.TryGetValue(gp, out var resolvedModName)
                                 && relevantMinionMods.Contains(resolvedModName))
                             {
                                  effectiveModName = resolvedModName;
                                  isRelevantMod = true;
                                  break;
                             }
                          }
                      }

                      if (isRelevantMod || isRelevantOption)
                       {
                            // Update the file entry with the effective names if they differ
                            if (!string.IsNullOrEmpty(effectiveModName)
                                && !string.Equals(f.ModName, effectiveModName, StringComparison.Ordinal))
                            {
                                f.ModName = effectiveModName;
                            }

                            if (!string.IsNullOrEmpty(effectiveOptionName)
                                && !string.Equals(f.OptionName, effectiveOptionName, StringComparison.Ordinal))
                            {
                                f.OptionName = effectiveOptionName;
                            }

                            Logger.LogDebug("Displacing SCD {paths} from Player to MinionOrMount (Mod: {mod})", string.Join(", ", f.GamePaths), effectiveModName);
                            displacedScds.Add(f);
                       }
                 }
             }
        }

        var kindsToProcess = charaData.FileReplacements.Keys.ToList();
        if (displacedScds.Count > 0 && !kindsToProcess.Contains(ObjectKind.MinionOrMount))
        {
            kindsToProcess.Add(ObjectKind.MinionOrMount);
        }

        foreach (var kind in kindsToProcess)
        {
            Dictionary<string, FileDataEntry> data = new(StringComparer.OrdinalIgnoreCase);
            
            var sourceFiles = charaData.FileReplacements.TryGetValue(kind, out var list) ? list : new();
            var filesToProcess = sourceFiles.AsEnumerable();

            if (kind == ObjectKind.Player)
            {
                // Remove displaced SCDs from Player
                filesToProcess = filesToProcess.Except(displacedScds);
            }
            else if (kind == ObjectKind.MinionOrMount)
            {
                // Add displaced SCDs to Minion
                filesToProcess = filesToProcess.Concat(displacedScds);
            }

            foreach (var fileEntry in filesToProcess)
            {
                if (kind == ObjectKind.Player)
                {
                     // Exclude explicit Minion/Mount/Companion paths from Player tab
                     var isMinionPath = fileEntry.GamePaths.Any(p =>
                        p.StartsWith("chara/monster/", StringComparison.OrdinalIgnoreCase) ||
                        p.StartsWith("chara/companion/", StringComparison.OrdinalIgnoreCase) ||
                        p.StartsWith("chara/demihuman/", StringComparison.OrdinalIgnoreCase));
                     
                     if (isMinionPath) continue;

                     // Exclude SCD files that belong to a Minion/Mount Option
                     // (Already handled by displacedScds logic above, but kept for safety)
                     var isScd = fileEntry.GamePaths.Any(p => p.EndsWith(".scd", StringComparison.OrdinalIgnoreCase));
                     if (isScd)
                     {
                         var optionKey = $"{fileEntry.ModName}|{fileEntry.OptionName}";
                         if (relevantMinionOptions.Contains(optionKey))
                         {
                             continue;
                         }
                     }
                }

                if (kind == ObjectKind.MinionOrMount && !displacedScds.Contains(fileEntry))
                {
                    // If the file was explicitly displaced (e.g. SCDs found in Player tab but belonging to Minion),
                    // we skip standard filtering to ensure it appears here.
                    // Filter files to ensure only relevant minion/mount files are shown
                    var isExplicitMinionPath = fileEntry.GamePaths.Any(p =>
                        p.StartsWith("chara/companion/", StringComparison.OrdinalIgnoreCase) ||
                        p.StartsWith("chara/monster/", StringComparison.OrdinalIgnoreCase) ||
                        p.StartsWith("chara/demihuman/", StringComparison.OrdinalIgnoreCase) ||
                        p.StartsWith("sound/voice/mon_", StringComparison.OrdinalIgnoreCase) ||
                        p.StartsWith("vfx/monster/", StringComparison.OrdinalIgnoreCase) ||
                        p.StartsWith("vfx/demihuman/", StringComparison.OrdinalIgnoreCase));

                    // Check if the file belongs to a mod that is considered a "Minion Mod"
                    // If active, it is inherently relevant, but we still check against player paths below
                    var isRelevantMod = fileEntry.IsActive || (!string.IsNullOrEmpty(fileEntry.ModName) && relevantMinionMods.Contains(fileEntry.ModName));

                    // Identify Player-specific paths to exclude from Minion tab
                    var isPlayerPath = fileEntry.GamePaths.Any(p =>
                        p.StartsWith("chara/human/", StringComparison.OrdinalIgnoreCase) ||
                        p.StartsWith("chara/equipment/", StringComparison.OrdinalIgnoreCase) ||
                        p.StartsWith("chara/weapon/", StringComparison.OrdinalIgnoreCase) ||
                        p.StartsWith("chara/accessory/", StringComparison.OrdinalIgnoreCase) ||
                        p.StartsWith("sound/voice/vo_", StringComparison.OrdinalIgnoreCase) ||
                        p.StartsWith("ui/", StringComparison.OrdinalIgnoreCase) ||
                        p.StartsWith("bg/", StringComparison.OrdinalIgnoreCase) ||
                        p.StartsWith("music/", StringComparison.OrdinalIgnoreCase) ||
                        p.StartsWith("cut/", StringComparison.OrdinalIgnoreCase));

                    // Special rule for SCD files in relevant options
                    // If an option contains monster paths, any SCD in that same option (regardless of path) belongs to the minion.
                    var isScd = fileEntry.GamePaths.Any(p => p.EndsWith(".scd", StringComparison.OrdinalIgnoreCase));
                    var optionKey = $"{fileEntry.ModName}|{fileEntry.OptionName}";
                    var isInMinionOption = !string.IsNullOrEmpty(fileEntry.ModName) && !string.IsNullOrEmpty(fileEntry.OptionName) && relevantMinionOptions.Contains(optionKey);
                    var isRelevantScd = isScd && isInMinionOption;

                    // Force include relevant SCDs even if they look like player paths or irrelevant mods
                    if (isRelevantScd)
                    {
                        // It is relevant! Include it.
                    }
                    else
                    {
                        // Standard filtering logic
                        if (!isExplicitMinionPath && (!isRelevantMod || isPlayerPath)) continue;
                    }
                }

                token.ThrowIfCancellationRequested();

                var fileCacheEntries = _fileCacheManager.GetAllFileCachesByHash(fileEntry.Hash, ignoreCacheEntries: true, validate: false).ToList();
                if (fileCacheEntries.Count == 0) continue;

                var filePath = fileCacheEntries[0].ResolvedFilepath;
                FileInfo fi = new(filePath);
                string ext = "unk?";
                try
                {
                    ext = fi.Extension[1..];
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Could not identify extension for {path}", filePath);
                }

                var tris = await _xivDataAnalyzer.GetTrianglesByHash(fileEntry.Hash).ConfigureAwait(false);

                foreach (var entry in fileCacheEntries)
                {
                    data[fileEntry.Hash] = new FileDataEntry(fileEntry.Hash, ext,
                        [.. fileEntry.GamePaths],
                        fileCacheEntries.Select(c => c.ResolvedFilepath).Distinct(StringComparer.Ordinal).ToList(),
                        entry.Size > 0 ? entry.Size.Value : 0,
                        entry.CompressedSize > 0 ? entry.CompressedSize.Value : 0,
                        tris,
                        fileEntry.IsActive);
                }
            }

            lock (_analysisLock)
            {
                LastAnalysis[kind] = data;
            }
        }

        Mediator.Publish(new CharacterDataAnalyzedMessage());

        _lastDataHash = charaData.DataHash.Value;
    }

    private void PrintAnalysis()
    {
        Dictionary<ObjectKind, Dictionary<string, FileDataEntry>> snapshot;
        lock (_analysisLock)
        {
            if (LastAnalysis.Count == 0) return;
            snapshot = LastAnalysis.ToDictionary(k => k.Key, v => v.Value);
        }

        foreach (var kvp in snapshot)
        {
            int fileCounter = 1;
            int totalFiles = kvp.Value.Count;
            Logger.LogInformation("=== Analysis for {obj} ===", kvp.Key);

            foreach (var entry in kvp.Value.OrderBy(b => b.Value.GamePaths.OrderBy(p => p, StringComparer.Ordinal).First(), StringComparer.Ordinal))
            {
                Logger.LogInformation("File {x}/{y}: {hash} (Active: {active})", fileCounter++, totalFiles, entry.Key, entry.Value.IsActive);
                foreach (var path in entry.Value.GamePaths)
                {
                    Logger.LogInformation("  Game Path: {path}", path);
                }
                if (entry.Value.FilePaths.Count > 1) Logger.LogInformation("  Multiple fitting files detected for {key}", entry.Key);
                foreach (var filePath in entry.Value.FilePaths)
                {
                    Logger.LogInformation("  File Path: {path}", filePath);
                }
                Logger.LogInformation("  Size: {size}, Compressed: {compressed}", UiSharedService.ByteToString(entry.Value.OriginalSize),
                    UiSharedService.ByteToString(entry.Value.CompressedSize));
            }
        }
        foreach (var kvp in snapshot)
        {
            Logger.LogInformation("=== Detailed summary by file type for {obj} ===", kvp.Key);
            foreach (var entry in kvp.Value.Select(v => v.Value).GroupBy(v => v.FileType, StringComparer.Ordinal))
            {
                Logger.LogInformation("{ext} files: {count}, size extracted: {size}, size compressed: {sizeComp}", entry.Key, entry.Count(),
                    UiSharedService.ByteToString(entry.Sum(v => v.OriginalSize)), UiSharedService.ByteToString(entry.Sum(v => v.CompressedSize)));
            }
            Logger.LogInformation("=== Total summary for {obj} ===", kvp.Key);
            Logger.LogInformation("Total files: {count}, size extracted: {size}, size compressed: {sizeComp}", kvp.Value.Count,
            UiSharedService.ByteToString(kvp.Value.Sum(v => v.Value.OriginalSize)), UiSharedService.ByteToString(kvp.Value.Sum(v => v.Value.CompressedSize)));
        }

        Logger.LogInformation("=== Total summary for all currently present objects ===");
        Logger.LogInformation("Total files: {count}, size extracted: {size}, size compressed: {sizeComp}",
            snapshot.Values.Sum(v => v.Values.Count),
            UiSharedService.ByteToString(snapshot.Values.Sum(c => c.Values.Sum(v => v.OriginalSize))),
            UiSharedService.ByteToString(snapshot.Values.Sum(c => c.Values.Sum(v => v.CompressedSize))));
        Logger.LogInformation("IMPORTANT NOTES:\n\r- For Sphene up- and downloads only the compressed size is relevant.\n\r- An unusually high total files count beyond 200 and up will also increase your download time to others significantly.");
    }

    internal sealed record FileDataEntry(string Hash, string FileType, List<string> GamePaths, List<string> FilePaths, long OriginalSize, long CompressedSize, long Triangles, bool IsActive)
    {
        public bool IsComputed => OriginalSize > 0 && CompressedSize > 0;
        public async Task ComputeSizes(FileCacheManager fileCacheManager, CancellationToken token)
        {
            var compressedsize = await fileCacheManager.GetCompressedFileData(Hash, token).ConfigureAwait(false);
            var normalSize = new FileInfo(FilePaths[0]).Length;
            var entries = fileCacheManager.GetAllFileCachesByHash(Hash, ignoreCacheEntries: true, validate: false);
            foreach (var entry in entries)
            {
                entry.Size = normalSize;
                entry.CompressedSize = compressedsize.Item2.LongLength;
            }
            OriginalSize = normalSize;
            CompressedSize = compressedsize.Item2.LongLength;
        }
        public long OriginalSize { get; private set; } = OriginalSize;
        public long CompressedSize { get; private set; } = CompressedSize;
        public long Triangles { get; private set; } = Triangles;

        public Lazy<string> Format = new(() =>
        {
            switch (FileType)
            {
                case "tex":
                    {
                        try
                        {
                            using var stream = new FileStream(FilePaths[0], FileMode.Open, FileAccess.Read, FileShare.Read);
                            using var reader = new BinaryReader(stream);
                            reader.BaseStream.Position = 4;
                            var format = (TexFile.TextureFormat)reader.ReadInt32();
                            return format.ToString();
                        }
                        catch
                        {
                            return "Unknown";
                        }
                    }
                default:
                    return string.Empty;
            }
        });
    }
}
