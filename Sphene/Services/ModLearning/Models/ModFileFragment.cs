using Sphene.PlayerData.Data;

namespace Sphene.Services.ModLearning.Models;

public class ModFileFragment
{
    public HashSet<FileReplacement> FileReplacements { get; set; } = [];
    public Dictionary<uint, HashSet<FileReplacement>> JobFileReplacements { get; set; } = [];
}
