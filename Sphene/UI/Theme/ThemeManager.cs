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

            // Copy current icon theme settings from config into the theme before saving
            if (_configService != null)
            {
                CopyIconSettingsFromConfigToTheme(_configService.Current, theme);
            }

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

            // Apply icon theme settings from loaded theme to config
            if (_configService != null && theme != null)
            {
                CopyIconSettingsFromThemeToConfig(theme, _configService.Current);
                _configService.Save();
            }

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

    private static void CopyIconSettingsFromConfigToTheme(SpheneConfiguration.Configurations.SpheneConfig config, ThemeConfiguration theme)
    {
        theme.IconGlobalAlpha = config.IconGlobalAlpha;
        theme.IconRainbowSpeed = config.IconRainbowSpeed;
        theme.IconShowModTransferBadge = config.IconShowModTransferBadge;
        theme.IconShowPairRequestBadge = config.IconShowPairRequestBadge;
        theme.IconShowNotificationBadge = config.IconShowNotificationBadge;
        theme.IconPermColor = config.IconPermColor;
        theme.IconPermAlpha = config.IconPermAlpha;
        theme.IconPermEffectPulse = config.IconPermEffectPulse;
        theme.IconPermEffectGlow = config.IconPermEffectGlow;
        theme.IconPermEffectBounce = config.IconPermEffectBounce;
        theme.IconPermEffectRainbow = config.IconPermEffectRainbow;
        theme.IconPermPulseMinRadius = config.IconPermPulseMinRadius;
        theme.IconPermPulseMaxRadius = config.IconPermPulseMaxRadius;
        theme.IconPermGlowIntensity = config.IconPermGlowIntensity;
        theme.IconPermGlowRadius = config.IconPermGlowRadius;
        theme.IconPermBounceIntensity = config.IconPermBounceIntensity;
        theme.IconPermBounceSpeed = config.IconPermBounceSpeed;
        theme.IconModTransferColor = config.IconModTransferColor;
        theme.IconModTransferAlpha = config.IconModTransferAlpha;
        theme.IconModTransferEffectPulse = config.IconModTransferEffectPulse;
        theme.IconModTransferEffectGlow = config.IconModTransferEffectGlow;
        theme.IconModTransferEffectBounce = config.IconModTransferEffectBounce;
        theme.IconModTransferEffectRainbow = config.IconModTransferEffectRainbow;
        theme.IconModTransferPulseMinRadius = config.IconModTransferPulseMinRadius;
        theme.IconModTransferPulseMaxRadius = config.IconModTransferPulseMaxRadius;
        theme.IconModTransferGlowIntensity = config.IconModTransferGlowIntensity;
        theme.IconModTransferGlowRadius = config.IconModTransferGlowRadius;
        theme.IconModTransferBounceIntensity = config.IconModTransferBounceIntensity;
        theme.IconModTransferBounceSpeed = config.IconModTransferBounceSpeed;
        theme.IconPairRequestColor = config.IconPairRequestColor;
        theme.IconPairRequestAlpha = config.IconPairRequestAlpha;
        theme.IconPairRequestEffectPulse = config.IconPairRequestEffectPulse;
        theme.IconPairRequestEffectGlow = config.IconPairRequestEffectGlow;
        theme.IconPairRequestEffectBounce = config.IconPairRequestEffectBounce;
        theme.IconPairRequestEffectRainbow = config.IconPairRequestEffectRainbow;
        theme.IconPairRequestPulseMinRadius = config.IconPairRequestPulseMinRadius;
        theme.IconPairRequestPulseMaxRadius = config.IconPairRequestPulseMaxRadius;
        theme.IconPairRequestGlowIntensity = config.IconPairRequestGlowIntensity;
        theme.IconPairRequestGlowRadius = config.IconPairRequestGlowRadius;
        theme.IconPairRequestBounceIntensity = config.IconPairRequestBounceIntensity;
        theme.IconPairRequestBounceSpeed = config.IconPairRequestBounceSpeed;
        theme.IconNotificationColor = config.IconNotificationColor;
        theme.IconNotificationAlpha = config.IconNotificationAlpha;
        theme.IconNotificationEffectPulse = config.IconNotificationEffectPulse;
        theme.IconNotificationEffectGlow = config.IconNotificationEffectGlow;
        theme.IconNotificationEffectBounce = config.IconNotificationEffectBounce;
        theme.IconNotificationEffectRainbow = config.IconNotificationEffectRainbow;
        theme.IconNotificationPulseMinRadius = config.IconNotificationPulseMinRadius;
        theme.IconNotificationPulseMaxRadius = config.IconNotificationPulseMaxRadius;
        theme.IconNotificationGlowIntensity = config.IconNotificationGlowIntensity;
        theme.IconNotificationGlowRadius = config.IconNotificationGlowRadius;
        theme.IconNotificationBounceIntensity = config.IconNotificationBounceIntensity;
        theme.IconNotificationBounceSpeed = config.IconNotificationBounceSpeed;
        theme.IconModTransferEffectDurationSeconds = config.IconModTransferEffectDurationSeconds;
        theme.IconPairRequestEffectDurationSeconds = config.IconPairRequestEffectDurationSeconds;
        theme.IconNotificationEffectDurationSeconds = config.IconNotificationEffectDurationSeconds;
        theme.IconModTransferBadgeDurationSeconds = config.IconModTransferBadgeDurationSeconds;
        theme.IconPairRequestBadgeDurationSeconds = config.IconPairRequestBadgeDurationSeconds;
        theme.IconNotificationBadgeDurationSeconds = config.IconNotificationBadgeDurationSeconds;
    }

    private static void CopyIconSettingsFromThemeToConfig(ThemeConfiguration theme, SpheneConfiguration.Configurations.SpheneConfig config)
    {
        config.IconGlobalAlpha = theme.IconGlobalAlpha;
        config.IconRainbowSpeed = theme.IconRainbowSpeed;
        config.IconShowModTransferBadge = theme.IconShowModTransferBadge;
        config.IconShowPairRequestBadge = theme.IconShowPairRequestBadge;
        config.IconShowNotificationBadge = theme.IconShowNotificationBadge;
        config.IconPermColor = theme.IconPermColor;
        config.IconPermAlpha = theme.IconPermAlpha;
        config.IconPermEffectPulse = theme.IconPermEffectPulse;
        config.IconPermEffectGlow = theme.IconPermEffectGlow;
        config.IconPermEffectBounce = theme.IconPermEffectBounce;
        config.IconPermEffectRainbow = theme.IconPermEffectRainbow;
        config.IconPermPulseMinRadius = theme.IconPermPulseMinRadius;
        config.IconPermPulseMaxRadius = theme.IconPermPulseMaxRadius;
        config.IconPermGlowIntensity = theme.IconPermGlowIntensity;
        config.IconPermGlowRadius = theme.IconPermGlowRadius;
        config.IconPermBounceIntensity = theme.IconPermBounceIntensity;
        config.IconPermBounceSpeed = theme.IconPermBounceSpeed;
        config.IconModTransferColor = theme.IconModTransferColor;
        config.IconModTransferAlpha = theme.IconModTransferAlpha;
        config.IconModTransferEffectPulse = theme.IconModTransferEffectPulse;
        config.IconModTransferEffectGlow = theme.IconModTransferEffectGlow;
        config.IconModTransferEffectBounce = theme.IconModTransferEffectBounce;
        config.IconModTransferEffectRainbow = theme.IconModTransferEffectRainbow;
        config.IconModTransferPulseMinRadius = theme.IconModTransferPulseMinRadius;
        config.IconModTransferPulseMaxRadius = theme.IconModTransferPulseMaxRadius;
        config.IconModTransferGlowIntensity = theme.IconModTransferGlowIntensity;
        config.IconModTransferGlowRadius = theme.IconModTransferGlowRadius;
        config.IconModTransferBounceIntensity = theme.IconModTransferBounceIntensity;
        config.IconModTransferBounceSpeed = theme.IconModTransferBounceSpeed;
        config.IconPairRequestColor = theme.IconPairRequestColor;
        config.IconPairRequestAlpha = theme.IconPairRequestAlpha;
        config.IconPairRequestEffectPulse = theme.IconPairRequestEffectPulse;
        config.IconPairRequestEffectGlow = theme.IconPairRequestEffectGlow;
        config.IconPairRequestEffectBounce = theme.IconPairRequestEffectBounce;
        config.IconPairRequestEffectRainbow = theme.IconPairRequestEffectRainbow;
        config.IconPairRequestPulseMinRadius = theme.IconPairRequestPulseMinRadius;
        config.IconPairRequestPulseMaxRadius = theme.IconPairRequestPulseMaxRadius;
        config.IconPairRequestGlowIntensity = theme.IconPairRequestGlowIntensity;
        config.IconPairRequestGlowRadius = theme.IconPairRequestGlowRadius;
        config.IconPairRequestBounceIntensity = theme.IconPairRequestBounceIntensity;
        config.IconPairRequestBounceSpeed = theme.IconPairRequestBounceSpeed;
        config.IconNotificationColor = theme.IconNotificationColor;
        config.IconNotificationAlpha = theme.IconNotificationAlpha;
        config.IconNotificationEffectPulse = theme.IconNotificationEffectPulse;
        config.IconNotificationEffectGlow = theme.IconNotificationEffectGlow;
        config.IconNotificationEffectBounce = theme.IconNotificationEffectBounce;
        config.IconNotificationEffectRainbow = theme.IconNotificationEffectRainbow;
        config.IconNotificationPulseMinRadius = theme.IconNotificationPulseMinRadius;
        config.IconNotificationPulseMaxRadius = theme.IconNotificationPulseMaxRadius;
        config.IconNotificationGlowIntensity = theme.IconNotificationGlowIntensity;
        config.IconNotificationGlowRadius = theme.IconNotificationGlowRadius;
        config.IconNotificationBounceIntensity = theme.IconNotificationBounceIntensity;
        config.IconNotificationBounceSpeed = theme.IconNotificationBounceSpeed;
        config.IconModTransferEffectDurationSeconds = theme.IconModTransferEffectDurationSeconds;
        config.IconPairRequestEffectDurationSeconds = theme.IconPairRequestEffectDurationSeconds;
        config.IconNotificationEffectDurationSeconds = theme.IconNotificationEffectDurationSeconds;
        config.IconModTransferBadgeDurationSeconds = theme.IconModTransferBadgeDurationSeconds;
        config.IconPairRequestBadgeDurationSeconds = theme.IconPairRequestBadgeDurationSeconds;
        config.IconNotificationBadgeDurationSeconds = theme.IconNotificationBadgeDurationSeconds;
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
