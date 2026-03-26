using Sphene.SpheneConfiguration.Configurations;

namespace Sphene.SpheneConfiguration;

public class PairCharacterCacheConfigService : ConfigurationServiceBase<PairCharacterCacheConfig>
{
    public const string ConfigName = "pair_character_cache.json";

    public PairCharacterCacheConfigService(string configDir) : base(configDir) { }
    public override string ConfigurationName => ConfigName;
}
