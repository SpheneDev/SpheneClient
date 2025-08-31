using Sphene.SpheneConfiguration.Configurations;

namespace Sphene.SpheneConfiguration;

public class SpheneConfigService : ConfigurationServiceBase<SpheneConfig>
{
    public const string ConfigName = "config.json";

    public SpheneConfigService(string configDir) : base(configDir)
    {
    }

    public override string ConfigurationName => ConfigName;
}
