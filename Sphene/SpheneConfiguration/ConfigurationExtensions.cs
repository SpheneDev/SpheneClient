using Sphene.SpheneConfiguration.Configurations;

namespace Sphene.SpheneConfiguration;

public static class ConfigurationExtensions
{
    public static bool HasValidSetup(this SpheneConfig configuration)
    {
        return configuration.AcceptedAgreement && configuration.InitialScanComplete
                    && !string.IsNullOrEmpty(configuration.CacheFolder)
                    && Directory.Exists(configuration.CacheFolder);
    }
}
