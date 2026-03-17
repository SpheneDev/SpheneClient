using Sphene.API.Data;

namespace Sphene.SpheneConfiguration.Configurations;

public class PairCharacterCacheConfig : ISpheneConfiguration
{
    public Dictionary<string, Dictionary<string, CachedPairCharacterData>> PairCharacterDataCache { get; set; } = [];
    public int Version { get; set; } = 1;

    public class CachedPairCharacterData
    {
        public CharacterData? CharacterData { get; set; }
        public DateTimeOffset LastUpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
        public string DataHash { get; set; } = string.Empty;
    }
}
