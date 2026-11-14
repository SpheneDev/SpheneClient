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
using Dalamud.Interface.Textures.TextureWraps;

namespace Sphene.Services;

public class UiFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly SpheneMediator _spheneMediator;
    private readonly ApiController _apiController;
    private readonly PairManager _pairManager;
    private readonly PerformanceCollectorService _performanceCollectorService;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly HousingOwnershipService _housingOwnershipService;
    private readonly SpheneConfigService _configService;
    private readonly SpheneProfileManager _spheneProfileManager;
    private readonly CommunityMediaClientService _communityMedia;
    private readonly LifestreamTravelService _lifestreamTravel;
    private readonly Interop.Ipc.IpcManager _ipcManager;

    public UiFactory(ILoggerFactory loggerFactory, SpheneMediator spheneMediator, ApiController apiController,
        PairManager pairManager, PerformanceCollectorService performanceCollectorService,
        DalamudUtilService dalamudUtilService, HousingOwnershipService housingOwnershipService,
        SpheneConfigService configService, SpheneProfileManager spheneProfileManager,
        CommunityMediaClientService communityMedia,
        LifestreamTravelService lifestreamTravel, Interop.Ipc.IpcManager ipcManager)
    {
        _loggerFactory = loggerFactory;
        _spheneMediator = spheneMediator;
        _apiController = apiController;
        _pairManager = pairManager;
        _performanceCollectorService = performanceCollectorService;
        _dalamudUtilService = dalamudUtilService;
        _housingOwnershipService = housingOwnershipService;
        _configService = configService;
        _spheneProfileManager = spheneProfileManager;
        _communityMedia = communityMedia;
        _lifestreamTravel = lifestreamTravel;
        _ipcManager = ipcManager;
    }

    public SyncshellAdminUI CreateSyncshellAdminUi(GroupFullInfoDto dto)
    {
        SyncshellCommunityView.MediaClient = _communityMedia;
        return new SyncshellAdminUI(_loggerFactory.CreateLogger<SyncshellAdminUI>(), _spheneMediator,
            _apiController, _pairManager, _dalamudUtilService, dto, _performanceCollectorService, _housingOwnershipService, _spheneProfileManager,
            _communityMedia, _lifestreamTravel, _ipcManager);
    }

    public PermissionWindowUI CreatePermissionPopupUi(Pair pair)
    {
        return new PermissionWindowUI(_loggerFactory.CreateLogger<PermissionWindowUI>(), pair,
            _spheneMediator, _apiController, _performanceCollectorService);
    }

    public SyncshellCommunityUI CreateSyncshellCommunityUi(SyncshellCommunityDto community, GroupFullInfoDto? group,
        bool joinPopup, bool previewAsVisitor = false)
    {
        SyncshellCommunityView.MediaClient = _communityMedia;
        return new SyncshellCommunityUI(_loggerFactory.CreateLogger<SyncshellCommunityUI>(), _spheneMediator,
            _configService, _apiController, _spheneProfileManager, _pairManager, community, group, joinPopup, _performanceCollectorService, _communityMedia,
            _dalamudUtilService, _lifestreamTravel, _ipcManager, previewAsVisitor);
    }

}
