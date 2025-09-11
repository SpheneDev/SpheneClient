using Sphene.PlayerData.Pairs;
using Sphene.SpheneConfiguration.Configurations;

namespace Sphene.SpheneConfiguration;

public class AcknowledgmentConfigService : ConfigurationServiceBase<AcknowledgmentConfiguration>
{
    public const string ConfigName = "acknowledgment.json";

    public AcknowledgmentConfigService(string configDir) : base(configDir)
    {
    }

    public override string ConfigurationName => ConfigName;
}