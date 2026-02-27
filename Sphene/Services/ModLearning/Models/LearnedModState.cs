using Sphene.PlayerData.Data;

using Sphene.API.Data.Enum;

namespace Sphene.Services.ModLearning.Models;

public class LearnedModState
{
    public string ModDirectoryName { get; set; } = string.Empty;
    public string? ModVersion { get; set; }
    public Dictionary<string, List<string>> Settings { get; set; } = []; // OptionName -> Values
    public Dictionary<ObjectKind, ModFileFragment> Fragments { get; set; } = [];
    public Dictionary<string, List<string>> ScdLinks { get; set; } = [];
    public Dictionary<string, string> PapEmotes { get; set; } = [];
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
