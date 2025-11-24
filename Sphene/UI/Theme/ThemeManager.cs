using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Sphene.SpheneConfiguration;

namespace Sphene.UI.Theme;

public static class ThemeManager
{
    private static string _themesDirectory = string.Empty;
    private static readonly string _oldThemesDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Sphene", "Themes");

    private static ILogger? _logger;
    private static IConfigService<SpheneConfiguration.Configurations.SpheneConfig>? _configService;

    public static void Initialize(ILogger logger, string configDirectory, IConfigService<SpheneConfiguration.Configurations.SpheneConfig>? configService = null)
    {
        _logger = logger;
        _configService = configService;
        _themesDirectory = Path.Combine(configDirectory, "Custom_themes");
        
        // Ensure themes directory exists
        if (!Directory.Exists(_themesDirectory))
        {
            Directory.CreateDirectory(_themesDirectory);
        }
        
        // Migrate existing themes from old location
        MigrateExistingThemes();
    }

    private static void MigrateExistingThemes()
    {
        try
        {
            if (Directory.Exists(_oldThemesDirectory))
            {
                var oldThemeFiles = Directory.GetFiles(_oldThemesDirectory, "*.json");
                if (oldThemeFiles.Length > 0)
                {
                    _logger?.LogDebug("Found {themeCount} themes to migrate from old location", oldThemeFiles.Length);
                    
                    foreach (var oldFile in oldThemeFiles)
                    {
                        var fileName = Path.GetFileName(oldFile);
                        var newFile = Path.Combine(_themesDirectory, fileName);
                        
                        if (!File.Exists(newFile))
                        {
                            File.Copy(oldFile, newFile);
                            _logger?.LogDebug("Migrated theme: {fileName}", fileName);
                        }
                    }
                    
                    _logger?.LogInformation("Successfully migrated {themeCount} themes to new location: {dir}", oldThemeFiles.Length, _themesDirectory);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to migrate existing themes from old location");
        }
    }

    public static async Task<bool> SaveTheme(ThemeConfiguration theme, string themeName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(themeName))
            {
                _logger?.LogWarning("Theme name cannot be empty");
                return false;
            }

            // Sanitize filename
            var sanitizedName = SanitizeFileName(themeName);
            var filePath = Path.Combine(_themesDirectory, $"{sanitizedName}.json");

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new Vector4JsonConverter(), new Vector2JsonConverter() }
            };
            var json = JsonSerializer.Serialize(theme, options);
            await File.WriteAllTextAsync(filePath, json).ConfigureAwait(false);
            _logger?.LogDebug("Theme '{themeName}' saved to '{filePath}'", themeName, filePath);
            
            // Save the selected theme name to configuration
            if (_configService != null)
            {
                _configService.Current.SelectedTheme = themeName;
                _configService.Save();
            }
            
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save theme '{themeName}'", themeName);
            return false;
        }
    }

    public static ThemeConfiguration? LoadTheme(string themeName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(themeName))
            {
                _logger?.LogWarning("Theme name cannot be empty");
                return null;
            }

            var sanitizedName = SanitizeFileName(themeName);
            var filePath = Path.Combine(_themesDirectory, $"{sanitizedName}.json");

            if (!File.Exists(filePath))
            {
                _logger?.LogWarning("Theme file not found: {filePath}", filePath);
                return null;
            }

            var json = File.ReadAllText(filePath);
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new Vector4JsonConverter(), new Vector2JsonConverter() }
            };

            var theme = JsonSerializer.Deserialize<ThemeConfiguration>(json, options);
            _logger?.LogInformation("Theme '{themeName}' loaded successfully from {filePath}", themeName, filePath);
            return theme;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load theme '{themeName}'", themeName);
            return null;
        }
    }

    public static string[] GetAvailableThemes()
    {
        try
        {
            if (!Directory.Exists(_themesDirectory))
                return Array.Empty<string>();

            var files = Directory.GetFiles(_themesDirectory, "*.json");
            var themeNames = new string[files.Length];

            for (int i = 0; i < files.Length; i++)
            {
                themeNames[i] = Path.GetFileNameWithoutExtension(files[i]);
            }

            return themeNames;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get available themes");
            return Array.Empty<string>();
        }
    }

    public static bool DeleteTheme(string themeName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(themeName))
            {
                _logger?.LogWarning("Theme name cannot be empty");
                return false;
            }

            var sanitizedName = SanitizeFileName(themeName);
            var filePath = Path.Combine(_themesDirectory, $"{sanitizedName}.json");
            var legacyFilePath = Path.Combine(_oldThemesDirectory, $"{sanitizedName}.json");

            if (!File.Exists(filePath))
            {
                _logger?.LogWarning("Theme file not found: {filePath}", filePath);
                return false;
            }

            File.Delete(filePath);
            _logger?.LogInformation("Theme '{themeName}' deleted successfully", themeName);
            try
            {
                if (File.Exists(legacyFilePath))
                {
                    File.Delete(legacyFilePath);
                    _logger?.LogDebug("Deleted legacy theme file: {legacyFilePath}", legacyFilePath);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to delete legacy theme file: {legacyFilePath}", legacyFilePath);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to delete theme '{themeName}'", themeName);
            return false;
        }
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        foreach (var c in invalidChars)
        {
            fileName = fileName.Replace(c, '_');
        }
        return fileName;
    }
    
    public static void SetSelectedTheme(string themeName)
    {
        if (_configService != null)
        {
            _configService.Current.SelectedTheme = themeName;
            _configService.Current.SelectedThemeName = themeName;
            _configService.Save();
        }
    }
    
    public static string GetSelectedTheme()
    {
        return _configService?.Current.SelectedTheme ?? "Default Sphene";
    }
    
    public static bool ShouldAutoLoadTheme()
    {
        return _configService?.Current.AutoLoadThemeOnStartup ?? true;
    }
}
