using Sphene.SpheneConfiguration.Models;

namespace Sphene.SpheneConfiguration.Configurations;

public class UidNotesConfig : ISpheneConfiguration
{
    public Dictionary<string, ServerNotesStorage> ServerNotes { get; set; } = new(StringComparer.Ordinal);
    public int Version { get; set; } = 0;
}
