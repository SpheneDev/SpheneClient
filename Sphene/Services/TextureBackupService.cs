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

            // Create backup with async file copy
            using var sourceStream = new FileStream(originalFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
            using var destinationStream = new FileStream(backupPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
            
            await sourceStream.CopyToAsync(destinationStream, cancellationToken);
            
            _logger.LogDebug("Backed up texture: {original} -> {backup}", originalFilePath, backupPath);
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
            var backupFiles = texFiles
                .Concat(zipFiles)
                .OrderByDescending(f => new FileInfo(f).CreationTime)
                .ToList();
                
            _logger.LogDebug("GetAvailableBackups: Found {count} .tex files in backup directory", backupFiles.Count);
            
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
                
                // Extract original filename by removing timestamp suffix
                // Format: originalname_20250925_134259.tex
                var parseName = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileNameWithoutExtension(fileName)
                    : fileName;
                var lastUnderscoreIndex = parseName.LastIndexOf('_');
                if (lastUnderscoreIndex > 0)
                {
                    var secondLastUnderscoreIndex = parseName.LastIndexOf('_', lastUnderscoreIndex - 1);
                    if (secondLastUnderscoreIndex > 0)
                    {
                        var originalName = parseName.Substring(0, secondLastUnderscoreIndex) + 
                            (parseName.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) ? ".tex" : Path.GetExtension(parseName));
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

            // Handle zipped backups (*.tex.zip) by extracting the single entry
            if (backupFilePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Run(() =>
                {
                    using var archive = ZipFile.OpenRead(backupFilePath);
                    var entry = archive.Entries.FirstOrDefault();
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
            _logger.LogDebug("Deleted {deletedCount}/{totalCount} backup files after restoration", deletedCount, successfulBackups.Count);
            try
            {
                DeleteEmptyBackupSubdirectories();
            }
            catch (Exception cleanupEx)
            {
                _logger.LogDebug(cleanupEx, "Failed to delete empty backup subdirectories");
            }
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

    private void DeleteEmptyBackupSubdirectories()
    {
        if (!Directory.Exists(_backupDirectory))
            return;

        var subdirs = Directory.GetDirectories(_backupDirectory, "*", SearchOption.AllDirectories);
        foreach (var dir in subdirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    var hasFiles = Directory.EnumerateFileSystemEntries(dir).Any();
                    if (!hasFiles)
                    {
                        Directory.Delete(dir, recursive: false);
                        _logger.LogDebug("Deleted empty backup subdirectory: {dir}", dir);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to delete empty subdirectory: {dir}", dir);
            }
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

}