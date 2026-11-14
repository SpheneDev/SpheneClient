using System.IO;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Microsoft.Extensions.Logging;
using Sphene.UI.Design;

namespace Sphene.UI;

public partial class UiSharedService
{
    private long _scanEtaT0;
    private long _scanEtaPrevT;
    private long _scanEtaPrevBytes;
    private long _scanEtaLogNext;
    private double _etaSmooth;
    private double _rateSmooth;
    private bool _cacheDirectoryHasOtherFilesThanCache = false;
    private bool _cacheDirectoryIsValidPath = true;
    private bool _isDirectoryWritable = false;
    private bool _isOneDrive = false;
    private bool _isPenumbraDirectory = false;
    private bool _downloadFolderIsValidPath = true;
    private bool _isDownloadFolderWritable = false;
    private bool _isDownloadFolderOneDrive = false;
    private bool _isDownloadFolderPenumbra = false;

    /// <summary>Checks whether a directory can be written to by creating a temporary file.</summary>
    public static bool IsDirectoryWritable(string dirPath, bool throwIfFails = false)
    {
        try
        {
            using FileStream fs = File.Create(
                       Path.Combine(
                           dirPath,
                           Path.GetRandomFileName()
                       ),
                       1,
                       FileOptions.DeleteOnClose);
            return true;
        }
        catch
        {
            if (throwIfFails)
                throw;

            return false;
        }
    }

    public void DrawCacheDirectorySetting()
    {
        SpheneUI.Text.ColoredWrapped("Note: The storage folder should be somewhere close to root (i.e. C:\\SpheneStorage) in a new empty folder. DO NOT point this to your game folder. DO NOT point this to your Penumbra folder.", ImGuiColors.DalamudYellow);
        var cacheDirectory = _configService.Current.CacheFolder;
        var monitoringActive = _cacheMonitor.SpheneWatcher != null;
        var (_, cacheBrowsePressed) = SpheneUI.Controls.InputTextWithButtons("Storage Folder##cache",
            ref cacheDirectory, 255, ImGuiInputTextFlags.ReadOnly,
            buttons: new SpheneUI.Controls.InputButtonSpec(FontAwesomeIcon.Folder,
                monitoringActive
                    ? "Stop the Monitoring before changing the Storage folder. As long as monitoring is active, you cannot change the Storage folder location."
                    : "Pick the Sphene storage folder.",
                Enabled: !monitoringActive));
        if (cacheBrowsePressed == 0)
        {
            FileDialogManager.OpenFolderDialog("Pick Sphene Storage Folder", (success, path) =>
            {
                if (!success) return;

                _isOneDrive = path.Contains("onedrive", StringComparison.OrdinalIgnoreCase);
                _isPenumbraDirectory = string.Equals(path.ToLowerInvariant(), _ipcManager.Penumbra.ModDirectory?.ToLowerInvariant(), StringComparison.Ordinal);
                var files = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
                _cacheDirectoryHasOtherFilesThanCache = false;
                var backupsRoot = Path.Combine(path, "texture_backups");
                foreach (var file in files)
                {
                    if (!string.IsNullOrEmpty(backupsRoot) && file.StartsWith(backupsRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.LogDebug("Ignoring backup file under texture_backups: {file}", file);
                        continue;
                    }
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    if (fileName.Length != 40 && !string.Equals(fileName, "desktop", StringComparison.OrdinalIgnoreCase))
                    {
                        _cacheDirectoryHasOtherFilesThanCache = true;
                        Logger.LogWarning("Found illegal file in {path}: {file}", path, file);
                        break;
                    }
                }
                var dirs = Directory.GetDirectories(path);
                var invalidDirs = dirs.Where(dir => !Path.GetFileName(dir).Equals("texture_backups", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (invalidDirs.Any())
                {
                    _cacheDirectoryHasOtherFilesThanCache = true;
                    Logger.LogWarning("Found folders in {path} not belonging to Sphene: {dirs}", path, string.Join(", ", invalidDirs));
                }

                _isDirectoryWritable = IsDirectoryWritable(path);
                _cacheDirectoryIsValidPath = PathRegex().IsMatch(path);

                if (!string.IsNullOrEmpty(path)
                    && Directory.Exists(path)
                    && _isDirectoryWritable
                    && !_isPenumbraDirectory
                    && !_isOneDrive
                    && !_cacheDirectoryHasOtherFilesThanCache
                    && _cacheDirectoryIsValidPath)
                {
                    _configService.Current.CacheFolder = path;
                    _configService.Save();
                    _cacheMonitor.StartSpheneWatcher(path);
                    _cacheMonitor.InvokeScan();
                }
            }, _dalamudUtil.IsWine ? @"Z:\" : @"C:\");
        }
        SpheneUI.Controls.HelpText("Folder where Sphene stores all synced mod files. Pick an empty folder close to the drive root.");

        if (_isPenumbraDirectory)
        {
            SpheneUI.Text.ColoredWrapped("Do not point the storage path directly to the Penumbra directory. If necessary, make a subfolder in it.", ImGuiColors.DalamudRed);
        }
        else if (_isOneDrive)
        {
            SpheneUI.Text.ColoredWrapped("Do not point the storage path to a folder in OneDrive. Do not use OneDrive folders for any Mod related functionality.", ImGuiColors.DalamudRed);
        }
        else if (!_isDirectoryWritable)
        {
            SpheneUI.Text.ColoredWrapped("The folder you selected does not exist or cannot be written to. Please provide a valid path.", ImGuiColors.DalamudRed);
        }
        else if (_cacheDirectoryHasOtherFilesThanCache)
        {
            SpheneUI.Text.ColoredWrapped("Your selected directory has files or directories inside that are not Sphene related. Use an empty directory or a previous Sphene storage directory only.", ImGuiColors.DalamudRed);
        }
        else if (!_cacheDirectoryIsValidPath)
        {
            SpheneUI.Text.ColoredWrapped("Your selected directory contains illegal characters unreadable by FFXIV. " +
                             "Restrict yourself to latin letters (A-Z), underscores (_), dashes (-) and arabic numbers (0-9).", ImGuiColors.DalamudRed);
        }

        float maxCacheSize = (float)_configService.Current.MaxLocalCacheInGiB;
        if (SpheneUI.Controls.SliderFloat("Maximum Storage Size in GiB", ref maxCacheSize, 1f, 200f, "%.2f GiB"))
        {
            _configService.Current.MaxLocalCacheInGiB = maxCacheSize;
            _configService.Save();
        }
        SpheneUI.Controls.HelpText("The storage is automatically governed by Sphene. It will clear itself automatically once it reaches the set capacity by removing the oldest unused files. You typically do not need to clear it yourself.");
    }

    public void DrawPenumbraModDownloadFolderSetting()
    {
        var downloadFolder = _configService.Current.PenumbraModDownloadFolder;
        var caps = string.IsNullOrEmpty(downloadFolder)
            ? new[] { new SpheneUI.Controls.InputButtonSpec(FontAwesomeIcon.Folder, "Pick the mod download folder.") }
            : new[]
            {
                new SpheneUI.Controls.InputButtonSpec(FontAwesomeIcon.Folder, "Pick the mod download folder."),
                new SpheneUI.Controls.InputButtonSpec(FontAwesomeIcon.Times, "Clear the mod download folder.")
            };
        var (_, downloadPressed) = SpheneUI.Controls.InputTextWithButtons("Download Folder##penumbra",
            ref downloadFolder, 255, ImGuiInputTextFlags.ReadOnly, buttons: caps);
        if (downloadPressed == 0)
        {
            FileDialogManager.OpenFolderDialog("Pick Mod Download Folder", (success, path) =>
            {
                if (!success) return;

                _isDownloadFolderOneDrive = path.Contains("onedrive", StringComparison.OrdinalIgnoreCase);
                _isDownloadFolderPenumbra = string.Equals(path.ToLowerInvariant(), _ipcManager.Penumbra.ModDirectory?.ToLowerInvariant(), StringComparison.Ordinal);

                _isDownloadFolderWritable = IsDirectoryWritable(path);
                _downloadFolderIsValidPath = PathRegex().IsMatch(path);

                if (!string.IsNullOrEmpty(path)
                    && _isDownloadFolderWritable
                    && !_isDownloadFolderPenumbra
                    && !_isDownloadFolderOneDrive
                    && _downloadFolderIsValidPath)
                {
                    _configService.Current.PenumbraModDownloadFolder = path;
                    _configService.Save();
                }
            }, _dalamudUtil.IsWine ? @"Z:\" : @"C:\");
        }
        else if (downloadPressed == 1)
        {
            _configService.Current.PenumbraModDownloadFolder = string.Empty;
            _configService.Save();
        }
        SpheneUI.Controls.HelpText("Optional: Select a folder where downloaded mod files (PMP) will be stored. If left empty, the default cache folder will be used.");

        if (_isDownloadFolderPenumbra)
        {
            SpheneUI.Text.ColoredWrapped("Do not point the download path directly to the Penumbra directory.", ImGuiColors.DalamudRed);
        }
        else if (_isDownloadFolderOneDrive)
        {
            SpheneUI.Text.ColoredWrapped("Do not point the download path to a folder in OneDrive.", ImGuiColors.DalamudRed);
        }
        else if (!string.IsNullOrEmpty(downloadFolder) && !_isDownloadFolderWritable)
        {
            SpheneUI.Text.ColoredWrapped("The folder you selected does not exist or cannot be written to. Please provide a valid path.", ImGuiColors.DalamudRed);
        }
        else if (!_downloadFolderIsValidPath)
        {
            SpheneUI.Text.ColoredWrapped("Your selected directory contains illegal characters unreadable by FFXIV. " +
                             "Restrict yourself to latin letters (A-Z), underscores (_), dashes (-) and arabic numbers (0-9).", ImGuiColors.DalamudRed);
        }

        if (string.IsNullOrEmpty(downloadFolder))
        {
            SpheneUI.Text.ColoredWrapped("No folder selected. Using default Sphene Storage folder.", ImGuiColors.HealerGreen);
        }
    }


















    public void DrawFileScanState()
    {
        ImGui.AlignTextToFramePadding();
        SpheneUI.Text.Plain("File Scanner Status");
        ImGui.SameLine();
        if (!_cacheMonitor.IsScanRunning) _scanEtaT0 = 0;
        if (_cacheMonitor.IsScanRunning)
        {
            // TotalFiles is the scan sentinel - 1 means "still collecting",
            // >1 the number of files to process. TotalFilesStorage counts
            // already-archived entries and can legitimately be 0.
            var total = _cacheMonitor.TotalFiles;
            var done = _cacheMonitor.CurrentFileProgress;
            var stored = _cacheMonitor.TotalFilesStorage;
            var bytesTotal = _cacheMonitor.TotalBytesToProcess;
            var bytesDone = _cacheMonitor.ProcessedBytes;

            SpheneUI.Text.Colored("Scanning", ImGuiColors.HealerGreen);

            // ETA on BYTES - hashing throughput scales with file size, so the
            // byte rate is far steadier than the file rate (which swings with
            // random file sizes). Windowed + EMA for display stability.
            if (total > 1 && bytesDone > 0)
            {
                var nowMs = Environment.TickCount64;
                if (_scanEtaT0 == 0)
                {
                    _scanEtaT0 = nowMs;
                    _scanEtaPrevT = nowMs;
                    _scanEtaPrevBytes = bytesDone;
                    _etaSmooth = 0;
                    _rateSmooth = 0;
                    Logger.LogInformation("[Scan] processing started: {total} files / {mb}MB, {stored} already in archive",
                        total, bytesTotal / 1048576L, stored);
                }

                if (nowMs - _scanEtaPrevT >= 1000)
                {
                    var instRate = (bytesDone - _scanEtaPrevBytes) / ((nowMs - _scanEtaPrevT) / 1000.0);
                    _rateSmooth = _rateSmooth <= 0 ? instRate : _rateSmooth * 0.7 + instRate * 0.3;
                    _scanEtaPrevT = nowMs;
                    _scanEtaPrevBytes = bytesDone;
                }

                if (_rateSmooth > 1 && bytesTotal > 0)
                {
                    var etaRaw = (bytesTotal - bytesDone) / _rateSmooth;
                    _etaSmooth = _etaSmooth <= 0 ? etaRaw : _etaSmooth * 0.6 + etaRaw * 0.4;
                    var etaText = _etaSmooth >= 60.0
                        ? $"ETA ~{(int)_etaSmooth / 60}:{(int)_etaSmooth % 60:D2} min"
                        : $"ETA ~{(int)Math.Ceiling(_etaSmooth)}s";
                    ImGui.SameLine(0f, 8f * SpheneUI.Scale);
                    SpheneUI.Text.Colored(etaText, ImGuiColors.DalamudGrey3);
                }

                if (nowMs >= _scanEtaLogNext)
                {
                    _scanEtaLogNext = nowMs + 2000;
                    var elapsed = (nowMs - _scanEtaT0) / 1000.0;
                    Logger.LogInformation("[Scan] done={done}/{total} ({doneMb}MB/{totalMb}MB) elapsed={elapsed:0.0}s rate={rate:0.0}MB/s eta={eta:0.0}s",
                        done, total, bytesDone / 1048576L, bytesTotal / 1048576L,
                        elapsed, _rateSmooth / 1048576.0, _etaSmooth);
                }
            }
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

            var barSize = new Vector2(ImGui.GetContentRegionAvail().X, 10f * SpheneUI.Scale);
            if (total > 1 && bytesTotal > 0)
            {
                var frac = Math.Clamp(bytesDone / (float)bytesTotal, 0f, 1f);
                SpheneUI.Controls.ProgressBar(frac, barSize, $"{done:N0} / {total:N0}  ·  {frac * 100f:0}%");
            }
            else if (total > 1)
            {
                var frac = Math.Clamp(done / (float)total, 0f, 1f);
                SpheneUI.Controls.ProgressBar(frac, barSize, $"{done:N0} / {total:N0}  ·  {frac * 100f:0}%");
            }
            else
            {
                SpheneUI.Controls.ProgressBarIndeterminate(barSize, "Collecting files…");
            }

            if (stored > 0)
            {
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                SpheneUI.Text.Colored($"{stored:N0} already in archive", ImGuiColors.DalamudGrey3);
            }
            SpheneUI.AttachTooltip("Note: it is possible to have more files in storage than scanned in, " +
                "this is due to the scanner normally ignoring those files but the game loading them in and using them on your character, so they get " +
                "added to the local storage.");
        }
        else if (_cacheMonitor.HaltScanLocks.Any(f => f.Value > 0))
        {
            ImGui.AlignTextToFramePadding();

            SpheneUI.Text.Plain("Halted (" + string.Join(", ", _cacheMonitor.HaltScanLocks.Where(f => f.Value > 0).Select(locker => locker.Key + ": " + locker.Value + " halt requests")) + ")");
            ImGui.SameLine();
            if (SpheneUI.Controls.Button("Reset halt requests##clearlocks"))
            {
                _cacheMonitor.ResetLocks();
            }
        }
        else
        {
            SpheneUI.Text.Plain("Idle");
            if (_configService.Current.InitialScanComplete)
            {
                ImGui.SameLine();
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Play, "Force rescan"))
                {
                    _cacheMonitor.InvokeScan();
                }
            }
        }
    }

    [GeneratedRegex(@"^(?:[a-zA-Z]:\\[\w\s\-\\]+?|\/(?:[\w\s\-\/])+?)$", RegexOptions.ECMAScript, 5000)]
    private static partial Regex PathRegex();
}
