using Sphene.SpheneConfiguration.Models;

namespace Sphene.SpheneConfiguration.Configurations;

public class ServerTagConfig : ISpheneConfiguration
{
    public Dictionary<string, ServerTagStorage> ServerTagStorage { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int Version { get; set; } = 0;
}
