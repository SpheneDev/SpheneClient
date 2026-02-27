namespace Sphene.Services.ModLearning.Models;

public class CharacterModProfile
{
    public string CharacterName { get; set; } = string.Empty;
    public string Homeworld { get; set; } = string.Empty;
    
    // Key: ModDirectoryName
    public Dictionary<string, List<LearnedModState>> LearnedMods { get; set; } = [];
}
