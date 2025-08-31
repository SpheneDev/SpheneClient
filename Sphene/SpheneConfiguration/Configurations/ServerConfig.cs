using Sphene.SpheneConfiguration.Models;
using Sphene.WebAPI;

namespace Sphene.SpheneConfiguration.Configurations;

[Serializable]
public class ServerConfig : ISpheneConfiguration
{
    public int CurrentServer { get; set; } = 0;

    public List<ServerStorage> ServerStorage { get; set; } = new()
    {
        { new ServerStorage() { ServerName = ApiController.MainServer, ServerUri = ApiController.MainServiceUri, UseOAuth2 = false } },
    };

    public bool SendCensusData { get; set; } = false;
    public bool ShownCensusPopup { get; set; } = false;

    public int Version { get; set; } = 2;
}
