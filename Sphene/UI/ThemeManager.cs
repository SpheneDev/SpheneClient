using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Sphene.SpheneConfiguration;

namespace Sphene.UI;

public static class ThemeManager
{
    private static readonly string ThemesDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Sphene", "Themes");

    private static ILogger? _logger;
    private static IConfigService<SpheneConfiguration.Configurations.SpheneConfig>? _configService;

    public static void Initialize(ILogger logger, IConfigService<SpheneConfiguration.Configurations.SpheneConfig>? configService = null)
    {
        _logger = logger;
        _configService = configService;
        
        // Ensure themes directory exists
        if (!Directory.Exists(ThemesDirectory))
        {
            Directory.CreateDirectory(ThemesDirectory);
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
            var filePath = Path.Combine(ThemesDirectory, $"{sanitizedName}.json");

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new Vector4JsonConverter(), new Vector2JsonConverter() }
            };
            var json = JsonSerializer.Serialize(theme, options);
            await File.WriteAllTextAsync(filePath, json);
            _logger?.LogDebug($"Theme '{themeName}' saved to '{filePath}'");
            
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
            _logger?.LogError(ex, $"Failed to save theme '{themeName}'");
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
            var filePath = Path.Combine(ThemesDirectory, $"{sanitizedName}.json");

            if (!File.Exists(filePath))
            {
                _logger?.LogWarning($"Theme file not found: {filePath}");
                return null;
            }

            var json = File.ReadAllText(filePath);
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new Vector4JsonConverter(), new Vector2JsonConverter() }
            };

            var theme = JsonSerializer.Deserialize<ThemeConfiguration>(json, options);
            _logger?.LogInformation($"Theme '{themeName}' loaded successfully from {filePath}");
            return theme;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, $"Failed to load theme '{themeName}'");
            return null;
        }
    }

    public static string[] GetAvailableThemes()
    {
        try
        {
            if (!Directory.Exists(ThemesDirectory))
                return Array.Empty<string>();

            var files = Directory.GetFiles(ThemesDirectory, "*.json");
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
            var filePath = Path.Combine(ThemesDirectory, $"{sanitizedName}.json");

            if (!File.Exists(filePath))
            {
                _logger?.LogWarning($"Theme file not found: {filePath}");
                return false;
            }

            File.Delete(filePath);
            _logger?.LogInformation($"Theme '{themeName}' deleted successfully");
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, $"Failed to delete theme '{themeName}'");
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

// Custom JSON converter for Vector4 since System.Numerics.Vector4 doesn't serialize well by default
public class Vector4JsonConverter : JsonConverter<System.Numerics.Vector4>
{
    public override System.Numerics.Vector4 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException();

        float x = 0, y = 0, z = 0, w = 0;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                var propertyName = reader.GetString();
                reader.Read();

                switch (propertyName?.ToLowerInvariant())
                {
                    case "x":
                        x = reader.GetSingle();
                        break;
                    case "y":
                        y = reader.GetSingle();
                        break;
                    case "z":
                        z = reader.GetSingle();
                        break;
                    case "w":
                        w = reader.GetSingle();
                        break;
                }
            }
        }

        return new System.Numerics.Vector4(x, y, z, w);
    }

    public override void Write(Utf8JsonWriter writer, System.Numerics.Vector4 value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("x", RoundSmallValue(value.X));
        writer.WriteNumber("y", RoundSmallValue(value.Y));
        writer.WriteNumber("z", RoundSmallValue(value.Z));
        writer.WriteNumber("w", RoundSmallValue(value.W));
        writer.WriteEndObject();
    }

    private static float RoundSmallValue(float value)
    {
        // Only round to zero if the value is extremely small (likely floating-point error)
        // Otherwise preserve the precision to maintain exact color values
        if (Math.Abs(value) < 1e-10f)
            return 0f;
        
        // For very small values that might serialize as scientific notation,
        // round to a reasonable precision (6 decimal places) to avoid E notation
        if (Math.Abs(value) < 1e-5f)
            return (float)Math.Round(value, 6);
            
        return value;
    }
}

// Custom JSON converter for Vector2 since System.Numerics.Vector2 doesn't serialize well by default
public class Vector2JsonConverter : JsonConverter<System.Numerics.Vector2>
{
    public override System.Numerics.Vector2 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException();

        float x = 0, y = 0;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                var propertyName = reader.GetString();
                reader.Read();

                switch (propertyName?.ToLowerInvariant())
                {
                    case "x":
                        x = reader.GetSingle();
                        break;
                    case "y":
                        y = reader.GetSingle();
                        break;
                }
            }
        }

        return new System.Numerics.Vector2(x, y);
    }

    public override void Write(Utf8JsonWriter writer, System.Numerics.Vector2 value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("x", RoundSmallValue(value.X));
        writer.WriteNumber("y", RoundSmallValue(value.Y));
        writer.WriteEndObject();
    }

    private static float RoundSmallValue(float value)
    {
        // Only round to zero if the value is extremely small (likely floating-point error)
        // Otherwise preserve the precision to maintain exact color values
        if (Math.Abs(value) < 1e-10f)
            return 0f;
        
        // For very small values that might serialize as scientific notation,
        // round to a reasonable precision (6 decimal places) to avoid E notation
        if (Math.Abs(value) < 1e-5f)
            return (float)Math.Round(value, 6);
            
        return value;
    }
}