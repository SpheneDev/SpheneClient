using Sphene.API.Dto.User;
using Sphene.PlayerData.Pairs;
using Sphene.Services.Mediator;
using Sphene.Services;
using Sphene.Services.ServerConfiguration;
using Sphene.SpheneConfiguration;
using Microsoft.Extensions.Logging;
using Sphene.WebAPI;

namespace Sphene.PlayerData.Factories;

public class PairFactory
{
    private readonly PairHandlerFactory _cachedPlayerFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SpheneMediator _spheneMediator;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly PlayerPerformanceConfigService _playerPerformanceConfigService;
    private readonly Lazy<ApiController> _apiController;
    private readonly VisibilityGateService _visibilityGateService;
    private readonly DalamudUtilService _dalamudUtilService;

    public PairFactory(ILoggerFactory loggerFactory, PairHandlerFactory cachedPlayerFactory,
        SpheneMediator spheneMediator, ServerConfigurationManager serverConfigurationManager,
        PlayerPerformanceConfigService playerPerformanceConfigService, Lazy<ApiController> apiController,
        VisibilityGateService visibilityGateService, DalamudUtilService dalamudUtilService)
    {
        _loggerFactory = loggerFactory;
        _cachedPlayerFactory = cachedPlayerFactory;
        _spheneMediator = spheneMediator;
        _serverConfigurationManager = serverConfigurationManager;
        _playerPerformanceConfigService = playerPerformanceConfigService;
        _apiController = apiController;
        _visibilityGateService = visibilityGateService;
        _dalamudUtilService = dalamudUtilService;
    }

    public Pair Create(UserFullPairDto userPairDto)
    {
        return new Pair(_loggerFactory.CreateLogger<Pair>(), userPairDto, _cachedPlayerFactory, _spheneMediator, _serverConfigurationManager, _playerPerformanceConfigService, _apiController, _visibilityGateService, _dalamudUtilService);
    }

    public Pair Create(UserPairDto userPairDto)
    {
        return new Pair(_loggerFactory.CreateLogger<Pair>(), new(userPairDto.User, userPairDto.IndividualPairStatus, [], userPairDto.OwnPermissions, userPairDto.OtherPermissions),
            _cachedPlayerFactory, _spheneMediator, _serverConfigurationManager, _playerPerformanceConfigService, _apiController, _visibilityGateService, _dalamudUtilService);
    }
}
