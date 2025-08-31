using Sphene.SpheneConfiguration.Configurations;

namespace Sphene.SpheneConfiguration;

public class NotesConfigService : ConfigurationServiceBase<UidNotesConfig>
{
    public const string ConfigName = "notes.json";

    public NotesConfigService(string configDir) : base(configDir)
    {
    }

    public override string ConfigurationName => ConfigName;
}
