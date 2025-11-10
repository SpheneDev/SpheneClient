using Sphene.API.Dto.Group;
using Sphene.PlayerData.Pairs;
using Sphene.Services.Mediator;
using Sphene.Services.ServerConfiguration;
using Sphene.SpheneConfiguration;
using Sphene.UI;
using Sphene.UI.Components;
using Sphene.UI.Components.Popup;
using Sphene.UI.Panels;
using Sphene.UI.Syncshell;
using Sphene.WebAPI;
using Microsoft.Extensions.Logging;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures.TextureWraps;

namespace Sphene.Services;

public class UiFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly SpheneMediator _spheneMediator;
    private readonly ApiController _apiController;
    private readonly UiSharedService _uiSharedService;
    private readonly PairManager _pairManager;
    private readonly ServerConfigurationManager _serverConfigManager;
    private readonly SpheneProfileManager _spheneProfileManager;
    private readonly PerformanceCollectorService _performanceCollectorService;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly FileDialogManager _fileDialogManager;
    private readonly HousingOwnershipService _housingOwnershipService;
    private readonly SpheneConfigService _configService;

    public UiFactory(ILoggerFactory loggerFactory, SpheneMediator spheneMediator, ApiController apiController,
        UiSharedService uiSharedService, PairManager pairManager, ServerConfigurationManager serverConfigManager,
        SpheneProfileManager spheneProfileManager, PerformanceCollectorService performanceCollectorService,
        DalamudUtilService dalamudUtilService, FileDialogManager fileDialogManager, HousingOwnershipService housingOwnershipService,
        SpheneConfigService configService)
    {
        _loggerFactory = loggerFactory;
        _spheneMediator = spheneMediator;
        _apiController = apiController;
        _uiSharedService = uiSharedService;
        _pairManager = pairManager;
        _serverConfigManager = serverConfigManager;
        _spheneProfileManager = spheneProfileManager;
        _performanceCollectorService = performanceCollectorService;
        _dalamudUtilService = dalamudUtilService;
        _fileDialogManager = fileDialogManager;
        _housingOwnershipService = housingOwnershipService;
        _configService = configService;
    }

    public SyncshellAdminUI CreateSyncshellAdminUi(GroupFullInfoDto dto)
    {
        return new SyncshellAdminUI(_loggerFactory.CreateLogger<SyncshellAdminUI>(), _spheneMediator,
            _apiController, _uiSharedService, _pairManager, _dalamudUtilService, dto, _performanceCollectorService, _fileDialogManager, this, _housingOwnershipService);
    }

    public StandaloneProfileUi CreateStandaloneProfileUi(Pair pair)
    {
        return new StandaloneProfileUi(_loggerFactory.CreateLogger<StandaloneProfileUi>(), _spheneMediator,
            _uiSharedService, _serverConfigManager, _spheneProfileManager, _pairManager, pair, _performanceCollectorService);
    }

    public PermissionWindowUI CreatePermissionPopupUi(Pair pair)
    {
        return new PermissionWindowUI(_loggerFactory.CreateLogger<PermissionWindowUI>(), pair,
            _spheneMediator, _uiSharedService, _apiController, _performanceCollectorService);
    }

    public SyncshellWelcomePageUI CreateSyncshellWelcomePageUi(SyncshellWelcomePageDto welcomePage, string groupName)
    {
        return new SyncshellWelcomePageUI(_loggerFactory.CreateLogger<SyncshellWelcomePageUI>(), _spheneMediator,
            _uiSharedService, _configService, welcomePage, groupName, _performanceCollectorService);
    }

    public WelcomePageLivePreviewUI CreateWelcomePageLivePreviewUi(PerformanceCollectorService performanceCollectorService)
    {
        return new WelcomePageLivePreviewUI(_loggerFactory.CreateLogger<WelcomePageLivePreviewUI>(), _spheneMediator,
            _uiSharedService, performanceCollectorService);
    }
}
