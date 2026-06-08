using System.Text.Json.Nodes;

namespace Sphene.SpheneConfiguration;

public class SpheneBackupDto
{
    public int BackupVersion { get; set; } = 1;
    public string ClientVersion { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public Dictionary<string, JsonNode> Configs { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string>? EncryptedConfigs { get; set; }
    public Dictionary<string, string>? CustomThemes { get; set; }
}
