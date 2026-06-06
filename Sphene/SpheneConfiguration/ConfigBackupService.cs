using Sphene.SpheneConfiguration.Configurations;
using Sphene.SpheneConfiguration.Models;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sphene.SpheneConfiguration;

public class ConfigBackupService
{
    private readonly IEnumerable<IConfigService<ISpheneConfiguration>> _configServices;
    private readonly ILogger<ConfigBackupService> _logger;
    private const int BackupVersion = 1;
    private const int Pbkdf2Iterations = 100000;

    public ConfigBackupService(
        IEnumerable<IConfigService<ISpheneConfiguration>> configServices,
        ILogger<ConfigBackupService> logger)
    {
        _configServices = configServices;
        _logger = logger;
    }

    public SpheneBackupDto CreateBackupDto(string? password)
    {
        var clientVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        var configs = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var encryptedConfigs = new Dictionary<string, string>(StringComparer.Ordinal);
        var options = new JsonSerializerOptions { WriteIndented = true };

        foreach (var configService in _configServices)
        {
            var name = configService.ConfigurationName;
            var node = JsonSerializer.SerializeToNode(configService.Current, configService.Current.GetType(), options);

            if (node == null)
                continue;

            if (!string.IsNullOrEmpty(password) && configService is ServerConfigService)
            {
                if (node is JsonObject serverObj)
                {
                    var strippedNode = serverObj.DeepClone();
                    var sensitivePayload = new JsonObject();

                    foreach (var key in new[] { "ServerStorage", "CurrentServer" })
                    {
                        if (strippedNode[key] is not null)
                        {
                            sensitivePayload[key] = strippedNode[key]?.DeepClone();
                            strippedNode[key] = null;
                        }
                    }

                    var sensitiveJson = sensitivePayload.ToJsonString();
                    var encrypted = EncryptString(sensitiveJson, password);
                    encryptedConfigs[name] = Convert.ToBase64String(encrypted);
                    configs[name] = strippedNode;
                }
                else
                {
                    configs[name] = node;
                }
            }
            else
            {
                configs[name] = node;
            }
        }

        return new SpheneBackupDto
        {
            BackupVersion = BackupVersion,
            ClientVersion = clientVersion,
            CreatedAt = DateTime.UtcNow,
            Configs = configs,
            EncryptedConfigs = encryptedConfigs.Count > 0 ? encryptedConfigs : null
        };
    }

    public void ExportToFile(string filePath, string? password)
    {
        var dto = CreateBackupDto(password);
        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json);
        _logger.LogInformation("Exported Sphene config backup to {path}", filePath);
    }

    public ImportResult ImportFromFile(string filePath, string? password, bool restoreCachePath, bool restoreShrinkUPath, ShrinkU.Configuration.ShrinkUConfigService? shrinkUConfigService = null)
    {
        var result = new ImportResult();

        if (!File.Exists(filePath))
        {
            result.ErrorMessage = "Backup file does not exist.";
            return result;
        }

        SpheneBackupDto? dto;
        try
        {
            var json = File.ReadAllText(filePath);
            dto = JsonSerializer.Deserialize<SpheneBackupDto>(json);
            if (dto == null)
            {
                result.ErrorMessage = "Failed to parse backup file.";
                return result;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read backup file");
            result.ErrorMessage = "Failed to read backup file: " + ex.Message;
            return result;
        }

        if (dto.BackupVersion > BackupVersion)
        {
            _logger.LogWarning("Backup version {backupVer} is newer than supported {supportedVer}", dto.BackupVersion, BackupVersion);
        }

        var configMap = _configServices.ToDictionary(
            c => c.ConfigurationName,
            c => c,
            StringComparer.Ordinal);

        foreach (var kvp in dto.Configs)
        {
            if (!configMap.TryGetValue(kvp.Key, out var configService))
            {
                _logger.LogWarning("Config {name} from backup not found in current config services, skipping", kvp.Key);
                result.SkippedCount++;
                continue;
            }

            try
            {
                var configType = configService.Current.GetType();
                var configJson = GetConfigJsonString(kvp.Value);
                var imported = JsonSerializer.Deserialize(configJson, configType);
                if (imported == null)
                {
                    result.SkippedCount++;
                    continue;
                }

                if (configService is ServerConfigService && dto.EncryptedConfigs != null
                    && dto.EncryptedConfigs.TryGetValue(kvp.Key, out var encryptedBase64)
                    && !string.IsNullOrEmpty(encryptedBase64))
                {
                    if (string.IsNullOrEmpty(password))
                    {
                        result.ErrorMessage = "This backup is password-protected. Please enter the password.";
                        return result;
                    }

                    try
                    {
                        var encryptedBytes = Convert.FromBase64String(encryptedBase64);
                        var decryptedJson = DecryptString(encryptedBytes, password);
                        var decryptedNode = JsonNode.Parse(decryptedJson);
                        var importedNode = JsonNode.Parse(JsonSerializer.Serialize(imported, configType));

                        if (decryptedNode is JsonObject decObj && importedNode is JsonObject impObj)
                        {
                            foreach (var prop in decObj)
                            {
                                impObj[prop.Key] = prop.Value?.DeepClone();
                            }

                            imported = JsonSerializer.Deserialize(GetConfigJsonString(impObj), configType);
                        }
                    }
                    catch (CryptographicException)
                    {
                        result.ErrorMessage = "Failed to decrypt backup. Password may be incorrect.";
                        return result;
                    }
                }

                if (configService is SpheneConfigService spheneCfg && imported is SpheneConfig spheneImported && !restoreCachePath)
                {
                    spheneImported.CacheFolder = spheneCfg.Current.CacheFolder;
                }

                var currentConfig = configService.Current;
                foreach (var prop in configType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!prop.CanRead || !prop.CanWrite)
                        continue;

                    var importedValue = prop.GetValue(imported);
                    prop.SetValue(currentConfig, importedValue);
                }

                configService.Save();
                result.RestoredCount++;
                _logger.LogInformation("Restored config {name}", kvp.Key);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restore config {name}", kvp.Key);
                result.FailedCount++;
            }
        }

        if (shrinkUConfigService != null && restoreShrinkUPath
            && dto.Configs.TryGetValue(SpheneConfigService.ConfigName, out var spheneNode))
        {
            try
            {
                if (spheneNode is JsonObject obj && obj["ExportFolder"] is JsonNode exportNode)
                {
                    var exportPath = exportNode.GetValue<string>();
                    if (!string.IsNullOrEmpty(exportPath))
                    {
                        shrinkUConfigService.Current.BackupFolderPath = exportPath;
                        shrinkUConfigService.Save();
                        _logger.LogInformation("Restored ShrinkU backup folder path to {path}", exportPath);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to restore ShrinkU path from backup");
            }
        }

        return result;
    }

    private static byte[] EncryptString(string plaintext, string password)
    {
        var salt = new byte[16];
        RandomNumberGenerator.Fill(salt);

        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        var iv = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 16);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        var result = new byte[salt.Length + encrypted.Length];
        Buffer.BlockCopy(salt, 0, result, 0, salt.Length);
        Buffer.BlockCopy(encrypted, 0, result, salt.Length, encrypted.Length);
        return result;
    }

    private static string DecryptString(byte[] encryptedData, string password)
    {
        var salt = new byte[16];
        Buffer.BlockCopy(encryptedData, 0, salt, 0, 16);

        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        var iv = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 16);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        var decrypted = decryptor.TransformFinalBlock(encryptedData, 16, encryptedData.Length - 16);
        return Encoding.UTF8.GetString(decrypted);
    }

    private static string GetConfigJsonString(JsonNode node)
    {
        if (node is JsonValue && node.GetValueKind() == JsonValueKind.String)
            return node.GetValue<string>() ?? string.Empty;
        return node.ToJsonString();
    }

    public static string GetBackupCacheFolderPath(SpheneBackupDto dto)
    {
        if (!dto.Configs.TryGetValue(SpheneConfigService.ConfigName, out var spheneNode))
            return string.Empty;

        try
        {
            if (spheneNode is JsonObject obj && obj["CacheFolder"] is JsonNode cacheNode)
                return cacheNode.GetValue<string>();
        }
        catch
        {
            // ignore
        }

        return string.Empty;
    }

    public static string GetBackupShrinkUPath(SpheneBackupDto dto)
    {
        if (!dto.Configs.TryGetValue(SpheneConfigService.ConfigName, out var spheneNode))
            return string.Empty;

        try
        {
            if (spheneNode is JsonObject obj && obj["ExportFolder"] is JsonNode exportNode)
                return exportNode.GetValue<string>();
        }
        catch
        {
            // ignore
        }

        return string.Empty;
    }
}

