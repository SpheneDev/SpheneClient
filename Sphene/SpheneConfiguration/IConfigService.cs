using Sphene.SpheneConfiguration.Configurations;

namespace Sphene.SpheneConfiguration;

public interface IConfigService<out T> : IDisposable where T : ISpheneConfiguration
{
    T Current { get; }
    string ConfigurationName { get; }
    string ConfigurationPath { get; }
    public event EventHandler? ConfigSave;
    void UpdateLastWriteTime();
}
