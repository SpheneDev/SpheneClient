using Microsoft.Extensions.Logging;
using Sphene.SpheneConfiguration;
using System.IO;
using System.IO.Compression;

namespace Sphene.Services;

public class TextureBackupService
{
    private readonly ILogger<TextureBackupService> _logger;
    private readonly SpheneConfigService _configService;
    private readonly string _backupDirectory;

    public TextureBackupService(ILogger<TextureBackupService> logger, SpheneConfigService configService)
    {
        _logger = logger;
        _configService = configService;
        _backupDirectory = Path.Combine(_configService.Current.CacheFolder, "texture_backups");
        
        // Don't create directory in constructor to avoid permission issues
        // Directory will be created when first needed
    }

    private void EnsureBackupDirectoryExists()
    {
        try
        {
            if (!Directory.Exists(_backupDirectory))
            {
                Directory.CreateDirectory(_backupDirectory);
                _logger.LogDebug("Created backup directory: {directory}", _backupDirectory);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create backup directory: {directory}", _backupDirectory);
            throw new InvalidOperationException($"Cannot create backup directory at {_backupDirectory}. Please ensure the cache folder is writable or change it in settings.", ex);
        }
    }

    public async Task<bool> BackupTextureAsync(string originalFilePath, CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureBackupDirectoryExists();
            
            if (!File.Exists(originalFilePath))
            {
                _logger.LogWarning("Original texture file does not exist: {path}", originalFilePath);
                return false;
            }

            var fileName = Path.GetFileName(originalFilePath);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var backupFileName = $"{Path.GetFileNameWithoutExtension(fileName)}_{timestamp}{Path.GetExtension(fileName)}";
            var backupPath = Path.Combine(_backupDirectory, backupFileName);

            if (_configService.Current.EnableZipCompressionForBackups)
            {
                var zipPath = backupPath + ".zip";
                await Task.Run(() =>
                {
                    using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
                    archive.CreateEntryFromFile(originalFilePath, backupFileName, CompressionLevel.Optimal);
                }, cancellationToken);

                _logger.LogDebug("Backed up texture with ZIP: {original} -> {zip}", originalFilePath, zipPath);
            }
            else
            {
                // Create backup with async file copy
                using var sourceStream = new FileStream(originalFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
                using var destinationStream = new FileStream(backupPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
                await sourceStream.CopyToAsync(destinationStream, cancellationToken);
                _logger.LogDebug("Backed up texture: {original} -> {backup}", originalFilePath, backupPath);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to backup texture file: {path}", originalFilePath);
            return false;
        }
    }

    public async Task<Dictionary<string, bool>> BackupTexturesAsync(IEnumerable<string> filePaths, IProgress<(string, int, int)> progress = null, CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<string, bool>();
        var fileList = filePaths.ToList();
        int completed = 0;
        int total = fileList.Count;

        foreach (var filePath in fileList)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            progress?.Report((filePath, ++completed, total));
            results[filePath] = await BackupTextureAsync(filePath, cancellationToken);
        }

        return results;
    }

    public void CleanupOldBackups(TimeSpan maxAge)
    {
        try
        {
            if (!Directory.Exists(_backupDirectory))
            {
                _logger.LogDebug("Backup directory does not exist, skipping cleanup: {directory}", _backupDirectory);
                return;
            }
            
            var cutoffDate = DateTime.Now - maxAge;
            var backupFiles = Directory.GetFiles(_backupDirectory, "*.tex")
                .Concat(Directory.GetFiles(_backupDirectory, "*.tex.zip"))
                .ToArray();

            foreach (var file in backupFiles)
            {
                var fileInfo = new FileInfo(file);
                if (fileInfo.CreationTime < cutoffDate)
                {
                    File.Delete(file);
                    _logger.LogDebug("Deleted old backup: {file}", file);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup old backups");
        }
    }

    public string GetBackupDirectory() => _backupDirectory;

    public long GetBackupDirectorySize()
    {
        try
        {
            if (!Directory.Exists(_backupDirectory))
            {
                _logger.LogDebug("Backup directory does not exist, returning size 0: {directory}", _backupDirectory);
                return 0;
            }
            
            var backupFiles = Directory.GetFiles(_backupDirectory, "*", SearchOption.AllDirectories);
            return backupFiles.Sum(file => new FileInfo(file).Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to calculate backup directory size");
            return 0;
        }
    }

    public List<string> GetAvailableBackups()
    {
        try
        {
            _logger.LogDebug("GetAvailableBackups: Checking backup directory: {directory}", _backupDirectory);
            
            if (!Directory.Exists(_backupDirectory))
            {
                _logger.LogDebug("Backup directory does not exist: {directory}", _backupDirectory);
                return new List<string>();
            }
            
            var texFiles = Directory.GetFiles(_backupDirectory, "*.tex");
            var zipFiles = Directory.GetFiles(_backupDirectory, "*.tex.zip");
            var backupFiles = texFiles.Concat(zipFiles)
                .OrderByDescending(f => new FileInfo(f).CreationTime)
                .ToList();
                
            _logger.LogDebug("GetAvailableBackups: Found {count} backup files (.tex/.tex.zip) in directory", backupFiles.Count);
            
            foreach (var file in backupFiles.Take(5)) // Log first 5 files for debugging
            {
                _logger.LogDebug("Backup file found: {file}", Path.GetFileName(file));
            }
            
            return backupFiles;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get available backups");
            return new List<string>();
        }
    }

    public Dictionary<string, List<string>> GetBackupsByOriginalFile()
    {
        var backupsByOriginal = new Dictionary<string, List<string>>();
        
        try
        {
            var backupFiles = GetAvailableBackups();
            _logger.LogDebug("GetBackupsByOriginalFile: Found {count} backup files in directory", backupFiles.Count);
            
            foreach (var backupFile in backupFiles)
            {
                var fileName = Path.GetFileName(backupFile);
                _logger.LogDebug("Processing backup file: {fileName}", fileName);
                
                // Normalize name for zipped backups by removing .zip
                var normalizedName = fileName.EndsWith(".tex.zip", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileNameWithoutExtension(fileName)
                    : fileName;

                // Extract original filename by removing timestamp suffix
                // Format: originalname_20250925_134259.tex / originalname_20250925_134259.tex.zip
                var lastUnderscoreIndex = normalizedName.LastIndexOf('_');
                if (lastUnderscoreIndex > 0)
                {
                    var secondLastUnderscoreIndex = normalizedName.LastIndexOf('_', lastUnderscoreIndex - 1);
                    if (secondLastUnderscoreIndex > 0)
                    {
                        var originalExt = Path.GetExtension(normalizedName);
                        var originalName = normalizedName.Substring(0, secondLastUnderscoreIndex) + originalExt;
                        _logger.LogDebug("Extracted original name: {originalName} from backup: {fileName}", originalName, fileName);
                        
                        if (!backupsByOriginal.ContainsKey(originalName))
                            backupsByOriginal[originalName] = new List<string>();
                        
                        backupsByOriginal[originalName].Add(backupFile);
                    }
                    else
                    {
                        _logger.LogDebug("Could not find second underscore in backup filename: {fileName}", fileName);
                    }
                }
                else
                {
                    _logger.LogDebug("Could not find underscore in backup filename: {fileName}", fileName);
                }
            }
            
            _logger.LogDebug("GetBackupsByOriginalFile: Grouped into {count} original files", backupsByOriginal.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to group backups by original file");
        }
        
        return backupsByOriginal;
    }

    public async Task<bool> RestoreTextureAsync(string backupFilePath, string targetFilePath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(backupFilePath))
            {
                _logger.LogWarning("Backup file does not exist: {path}", backupFilePath);
                return false;
            }

            // Ensure target directory exists
            var targetDirectory = Path.GetDirectoryName(targetFilePath);
            if (!string.IsNullOrEmpty(targetDirectory))
                Directory.CreateDirectory(targetDirectory);

            if (backupFilePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Run(() =>
                {
                    using var archive = ZipFile.OpenRead(backupFilePath);
                    var entry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
                                ?? archive.Entries.FirstOrDefault();
                    if (entry == null)
                        throw new InvalidOperationException($"ZIP backup has no entries: {backupFilePath}");
                    entry.ExtractToFile(targetFilePath, overwrite: true);
                }, cancellationToken);

                _logger.LogDebug("Restored texture from ZIP: {backup} -> {target}", backupFilePath, targetFilePath);
            }
            else
            {
                // Restore backup with async file copy
                using var sourceStream = new FileStream(backupFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
                using var destinationStream = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
                await sourceStream.CopyToAsync(destinationStream, cancellationToken);
                _logger.LogDebug("Restored texture: {backup} -> {target}", backupFilePath, targetFilePath);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore texture file: {backup} -> {target}", backupFilePath, targetFilePath);
            return false;
        }
    }

    public async Task<Dictionary<string, bool>> RestoreTexturesAsync(Dictionary<string, string> backupToTargetMap, IProgress<(string, int, int)> progress = null, CancellationToken cancellationToken = default)
    {
        return await RestoreTexturesAsync(backupToTargetMap, deleteBackupsAfterRestore: false, progress, cancellationToken);
    }

    public async Task<Dictionary<string, bool>> RestoreTexturesAsync(Dictionary<string, string> backupToTargetMap, bool deleteBackupsAfterRestore, IProgress<(string, int, int)> progress = null, CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<string, bool>();
        var successfulBackups = new List<string>();
        int completed = 0;
        int total = backupToTargetMap.Count;

        foreach (var kvp in backupToTargetMap)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            progress?.Report((kvp.Value, ++completed, total));
            var restoreSuccess = await RestoreTextureAsync(kvp.Key, kvp.Value, cancellationToken);
            results[kvp.Key] = restoreSuccess;
            
            if (restoreSuccess && deleteBackupsAfterRestore)
            {
                successfulBackups.Add(kvp.Key);
            }
        }

        // Delete backup files after successful restoration if requested
        if (deleteBackupsAfterRestore && successfulBackups.Count > 0)
        {
            _logger.LogDebug("Deleting {count} backup files after successful restoration", successfulBackups.Count);
            var deleteResults = await DeleteBackupFilesAsync(successfulBackups);
            
            var deletedCount = deleteResults.Values.Count(success => success);
            _logger.LogInformation("Deleted {deletedCount}/{totalCount} backup files after restoration", deletedCount, successfulBackups.Count);
        }

        return results;
    }

    public async Task<bool> DeleteBackupFileAsync(string backupFilePath)
    {
        try
        {
            if (File.Exists(backupFilePath))
            {
                await Task.Run(() => File.Delete(backupFilePath));
                _logger.LogDebug("Deleted backup file: {backupFile}", backupFilePath);
                return true;
            }
            else
            {
                _logger.LogWarning("Backup file not found for deletion: {backupFile}", backupFilePath);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete backup file: {backupFile}", backupFilePath);
            return false;
        }
    }

    public async Task<Dictionary<string, bool>> DeleteBackupFilesAsync(IEnumerable<string> backupFilePaths)
    {
        var results = new Dictionary<string, bool>();
        
        foreach (var backupFilePath in backupFilePaths)
        {
            results[backupFilePath] = await DeleteBackupFileAsync(backupFilePath);
        }
        
        return results;
    }

    public async Task<(int processedCount, int compressedCount, int deletedOriginalsCount, long freedSpace)> CompressExistingBackupsAsync(
        bool deleteOriginals = true,
        IProgress<(string currentFile, int completed, int total)> progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureBackupDirectoryExists();

            if (!Directory.Exists(_backupDirectory))
            {
                _logger.LogDebug("CompressExistingBackupsAsync: Backup directory does not exist: {directory}", _backupDirectory);
                return (0, 0, 0, 0);
            }

            var texFiles = Directory.GetFiles(_backupDirectory, "*.tex", SearchOption.TopDirectoryOnly)
                .OrderBy(f => new FileInfo(f).CreationTime)
                .ToList();

            int total = texFiles.Count;
            int processed = 0;
            int compressed = 0;
            int deleted = 0;
            long freedSpace = 0;

            await Task.Run(() =>
            {
                foreach (var texFile in texFiles)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    try
                    {
                        progress?.Report((texFile, ++processed, total));

                        var zipPath = texFile + ".zip";
                        if (File.Exists(zipPath))
                        {
                            _logger.LogDebug("ZIP already exists, skipping: {zipPath}", zipPath);
                            continue;
                        }

                        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                        {
                            // Store the original backup filename inside the ZIP
                            var entryName = Path.GetFileName(texFile);
                            archive.CreateEntryFromFile(texFile, entryName, CompressionLevel.Optimal);
                        }

                        compressed++;
                        _logger.LogDebug("Compressed backup to ZIP: {tex} -> {zip}", texFile, zipPath);

                        if (deleteOriginals)
                        {
                            var fileInfo = new FileInfo(texFile);
                            var size = fileInfo.Length;
                            File.Delete(texFile);
                            deleted++;
                            freedSpace += size;
                            _logger.LogDebug("Deleted original backup after compression: {tex} (freed {size} bytes)", texFile, size);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to compress backup file: {file}", texFile);
                    }
                }
            }, cancellationToken);

            _logger.LogDebug("Compression summary: processed={processed}, compressed={compressed}, deleted={deleted}, freed={freedSpace} bytes",
                processed, compressed, deleted, freedSpace);

            return (processed, compressed, deleted, freedSpace);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CompressExistingBackupsAsync failed");
            return (0, 0, 0, 0);
        }
    }

    // Detailed backup statistics model
    public class BackupStats
    {
        public long TotalSize { get; set; }
        public int TotalFiles { get; set; }
        public int TexCount { get; set; }
        public long TexSize { get; set; }
        public int ZipCount { get; set; }
        public long ZipSize { get; set; }
        public DateTime? Oldest { get; set; }
        public DateTime? Newest { get; set; }
        public int OriginalGroups { get; set; }
        public double AvgBackupsPerOriginal { get; set; }
        public List<(string FileName, long Size)> TopLargestFiles { get; set; } = new();
        public List<(string OriginalName, int Count)> TopOriginalsByBackupCount { get; set; } = new();
    }

    public async Task<BackupStats> GetBackupStatsAsync(int topN = 5)
    {
        try
        {
            EnsureBackupDirectoryExists();

            var stats = new BackupStats();

            if (!Directory.Exists(_backupDirectory))
            {
                _logger.LogDebug("GetBackupStatsAsync: Backup directory does not exist: {directory}", _backupDirectory);
                return stats;
            }

            var allFiles = Directory.GetFiles(_backupDirectory, "*", SearchOption.TopDirectoryOnly);
            var texFiles = Directory.GetFiles(_backupDirectory, "*.tex", SearchOption.TopDirectoryOnly);
            var zipFiles = Directory.GetFiles(_backupDirectory, "*.tex.zip", SearchOption.TopDirectoryOnly);

            stats.TotalFiles = allFiles.Length;
            stats.TexCount = texFiles.Length;
            stats.ZipCount = zipFiles.Length;

            DateTime? oldest = null;
            DateTime? newest = null;

            await Task.Run(() =>
            {
                foreach (var file in allFiles)
                {
                    try
                    {
                        var fi = new FileInfo(file);
                        stats.TotalSize += fi.Length;

                        if (!oldest.HasValue || fi.CreationTime < oldest.Value)
                            oldest = fi.CreationTime;
                        if (!newest.HasValue || fi.CreationTime > newest.Value)
                            newest = fi.CreationTime;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to read file info for backup: {file}", file);
                    }
                }

                foreach (var file in texFiles)
                {
                    try
                    {
                        var fi = new FileInfo(file);
                        stats.TexSize += fi.Length;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to read size for tex file: {file}", file);
                    }
                }

                foreach (var file in zipFiles)
                {
                    try
                    {
                        var fi = new FileInfo(file);
                        stats.ZipSize += fi.Length;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to read size for zip file: {file}", file);
                    }
                }

                // Top largest backup files
                var topLargest = allFiles
                    .Select(f =>
                    {
                        try { var fi = new FileInfo(f); return (f, fi.Length); }
                        catch { return (f, 0L); }
                    })
                    .OrderByDescending(t => t.Item2)
                    .Take(topN)
                    .Select(t => (Path.GetFileName(t.Item1), t.Item2))
                    .ToList();
                stats.TopLargestFiles = topLargest;
            });

            stats.Oldest = oldest;
            stats.Newest = newest;

            // Group by original file and compute counts
            try
            {
                var byOriginal = GetBackupsByOriginalFile();
                stats.OriginalGroups = byOriginal.Count;
                stats.AvgBackupsPerOriginal = byOriginal.Count == 0 ? 0 : byOriginal.Values.Average(v => v.Count);

                stats.TopOriginalsByBackupCount = byOriginal
                    .Select(kvp => (kvp.Key, kvp.Value.Count))
                    .OrderByDescending(t => t.Count)
                    .Take(topN)
                    .Select(t => (t.Key, t.Count))
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to compute grouping statistics for backups");
            }

            _logger.LogDebug(
                "Backup stats: totalFiles={totalFiles}, totalSize={totalSize} bytes, texFiles={texCount} ({texSize} bytes), zipFiles={zipCount} ({zipSize} bytes), originalGroups={groups}",
                stats.TotalFiles, stats.TotalSize, stats.TexCount, stats.TexSize, stats.ZipCount, stats.ZipSize, stats.OriginalGroups);

            return stats;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetBackupStatsAsync failed");
            return new BackupStats();
        }
    }

    public async Task<(long totalSize, int fileCount)> GetBackupStorageInfoAsync()
    {
        try
        {
            var backupDirectory = GetBackupDirectory();
            if (!Directory.Exists(backupDirectory))
            {
                return (0, 0);
            }

            var backupFiles = Directory.GetFiles(backupDirectory, "*", SearchOption.AllDirectories);
            long totalSize = 0;
            int fileCount = 0;

            await Task.Run(() =>
            {
                foreach (var file in backupFiles)
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        totalSize += fileInfo.Length;
                        fileCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to get size for backup file: {file}", file);
                    }
                }
            });

            return (totalSize, fileCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to calculate backup storage info");
            return (0, 0);
        }
    }

    public async Task<(int deletedCount, long freedSpace)> CleanupOldBackupsAsync(int daysOld = 3)
    {
        try
        {
            var backupDirectory = GetBackupDirectory();
            if (!Directory.Exists(backupDirectory))
            {
                return (0, 0);
            }

            var cutoffDate = DateTime.Now.AddDays(-daysOld);
            var backupFiles = Directory.GetFiles(backupDirectory, "*", SearchOption.AllDirectories);
            
            int deletedCount = 0;
            long freedSpace = 0;

            await Task.Run(() =>
            {
                foreach (var file in backupFiles)
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        if (fileInfo.CreationTime < cutoffDate)
                        {
                            var fileSize = fileInfo.Length;
                            File.Delete(file);
                            deletedCount++;
                            freedSpace += fileSize;
                            _logger.LogDebug("Deleted old backup file: {file} (created: {created})", file, fileInfo.CreationTime);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete old backup file: {file}", file);
                    }
                }
            });

            if (deletedCount > 0)
            {
                _logger.LogInformation("Cleanup completed: deleted {count} old backup files, freed {size:F2} MB", 
                    deletedCount, freedSpace / (1024.0 * 1024.0));
            }
            else
            {
                _logger.LogDebug("No old backup files found for cleanup");
            }

            return (deletedCount, freedSpace);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup old backup files");
            return (0, 0);
        }
    }
}