using Sphene.SpheneConfiguration.Configurations;

namespace Sphene.SpheneConfiguration;

public class ServerTagConfigService : ConfigurationServiceBase<ServerTagConfig>
{
    public const string ConfigName = "servertags.json";

    public ServerTagConfigService(string configDir) : base(configDir)
    {
    }

    public override string ConfigurationName => ConfigName;
}
