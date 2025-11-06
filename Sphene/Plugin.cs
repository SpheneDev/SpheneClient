using Dalamud.Game.ClientState.Objects;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Sphene.FileCache;
using Sphene.Interop;
using Sphene.Interop.Ipc;
using Sphene.SpheneConfiguration;
using Sphene.SpheneConfiguration.Configurations;
using Sphene.PlayerData.Factories;
using Sphene.PlayerData.Pairs;
using Sphene.PlayerData.Services;
using Sphene.Services;
using Sphene.Services.Events;
using Sphene.Services.Mediator;
using Sphene.Services.ServerConfiguration;
using Sphene.UI;
using Sphene.UI.Components;
using Sphene.UI.Components.Popup;
using Sphene.UI.Handlers;
using Sphene.WebAPI;
using Sphene.WebAPI.Files;
using Sphene.WebAPI.SignalR;
using Sphene.WebAPI.SignalR.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NReco.Logging.File;
using System.Net.Http.Headers;
using System.Reflection;
using Sphene.Services.CharaData;
using Dalamud.Game;
using ShrinkU.Configuration;
using ShrinkU.Interop;
using ShrinkU.UI;
using System;
using System.IO;

namespace Sphene;

public sealed class Plugin : IDalamudPlugin
{
    private readonly IHost _host;

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commandManager, IDataManager gameData,
        IFramework framework, IObjectTable objectTable, IClientState clientState, ICondition condition, IChatGui chatGui,
        IGameGui gameGui, IDtrBar dtrBar, IPluginLog pluginLog, ITargetManager targetManager, INotificationManager notificationManager,
        ITextureProvider textureProvider, IContextMenu contextMenu, IGameInteropProvider gameInteropProvider, IGameConfig gameConfig,
        ISigScanner sigScanner, IPartyList partyList)
    {
        if (!Directory.Exists(pluginInterface.ConfigDirectory.FullName))
            Directory.CreateDirectory(pluginInterface.ConfigDirectory.FullName);
        var traceDir = Path.Join(pluginInterface.ConfigDirectory.FullName, "tracelog");
        if (!Directory.Exists(traceDir))
            Directory.CreateDirectory(traceDir);

        foreach (var file in Directory.EnumerateFiles(traceDir)
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc).Skip(9))
        {
            int attempts = 0;
            bool deleted = false;
            while (!deleted && attempts < 5)
            {
                try
                {
                    file.Delete();
                    deleted = true;
                }
                catch
                {
                    attempts++;
                    Thread.Sleep(500);
                }
            }
        }

        _host = new HostBuilder()
        .UseContentRoot(pluginInterface.ConfigDirectory.FullName)
        .ConfigureLogging(lb =>
        {
            lb.ClearProviders();
            lb.AddDalamudLogging(pluginLog, gameData.HasModifiedGameDataFiles);
            lb.AddFile(Path.Combine(traceDir, $"sphene-trace-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.log"), (opt) =>
            {
                opt.Append = true;
                opt.RollingFilesConvention = FileLoggerOptions.FileRollingConvention.Ascending;
                opt.MinLevel = LogLevel.Trace;
                opt.FileSizeLimitBytes = 50 * 1024 * 1024;
            });
            lb.SetMinimumLevel(LogLevel.Trace);
        })
        .ConfigureServices(collection =>
        {
            collection.AddSingleton(new WindowSystem("Sphene"));
            collection.AddSingleton<FileDialogManager>();
            collection.AddSingleton(new Dalamud.Localization("Sphene.Localization.", "", useEmbedded: true));
            collection.AddSingleton(commandManager);
            collection.AddSingleton(framework);
            collection.AddSingleton(pluginInterface);

            // ShrinkU integration services and windows
            collection.AddSingleton<Microsoft.Extensions.Logging.ILogger>(s => s.GetRequiredService<ILoggerFactory>().CreateLogger("ShrinkU"));
            collection.AddSingleton<ShrinkUConfigService>(s =>
            {
                var logger = s.GetRequiredService<Microsoft.Extensions.Logging.ILogger>();
                var cfgSvc = new ShrinkUConfigService(pluginInterface, logger);
                try
                {
                    var spheneCfg = s.GetRequiredService<SpheneConfigService>();
                    var cache = spheneCfg.Current.CacheFolder;
                    if (!string.IsNullOrWhiteSpace(cache))
                    {
                        var target = Path.Combine(cache, "texture_backups");
                        var current = cfgSvc.Current.BackupFolderPath ?? string.Empty;
                        var defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ShrinkU", "Backups");
                        var isDefault = string.Equals(current, defaultPath, StringComparison.OrdinalIgnoreCase);

                        if (cfgSvc.Current.FirstRunCompleted == false || isDefault || string.IsNullOrWhiteSpace(current))
                        {
                            try { Directory.CreateDirectory(target); } catch { }
                            cfgSvc.Current.BackupFolderPath = target;
                            // auto-complete first run when we configure the backup path
                            cfgSvc.Current.FirstRunCompleted = true;
                            try { cfgSvc.Save(); } catch { }
                            logger.LogDebug("Configured ShrinkU backup path to Sphene texture_backups: {path}", target);
                        }
                    }
                }
                catch { }
                return cfgSvc;
            });
            collection.AddSingleton<ShrinkU.Services.PenumbraIpc>(s => new ShrinkU.Services.PenumbraIpc(pluginInterface, s.GetRequiredService<Microsoft.Extensions.Logging.ILogger>()));
            collection.AddSingleton<ShrinkU.Services.TextureBackupService>(s => new ShrinkU.Services.TextureBackupService(
                s.GetRequiredService<Microsoft.Extensions.Logging.ILogger>(),
                s.GetRequiredService<ShrinkUConfigService>(),
                s.GetRequiredService<ShrinkU.Services.PenumbraIpc>()));
            collection.AddSingleton<ShrinkU.Services.TextureConversionService>(s => new ShrinkU.Services.TextureConversionService(
                s.GetRequiredService<Microsoft.Extensions.Logging.ILogger>(),
                s.GetRequiredService<ShrinkU.Services.PenumbraIpc>(),
                s.GetRequiredService<ShrinkU.Services.TextureBackupService>(),
                s.GetRequiredService<ShrinkUConfigService>()));
            collection.AddSingleton<SettingsUI>(s => new SettingsUI(
                s.GetRequiredService<Microsoft.Extensions.Logging.ILogger>(),
                s.GetRequiredService<ShrinkUConfigService>(),
                s.GetRequiredService<ShrinkU.Services.TextureConversionService>()));
            collection.AddSingleton<ConversionUI>(s => new ConversionUI(
                s.GetRequiredService<Microsoft.Extensions.Logging.ILogger>(),
                s.GetRequiredService<ShrinkUConfigService>(),
                s.GetRequiredService<ShrinkU.Services.TextureConversionService>(),
                s.GetRequiredService<ShrinkU.Services.TextureBackupService>(),
                () => s.GetRequiredService<SettingsUI>().IsOpen = true));
            collection.AddSingleton<FirstRunSetupUI>(s =>
            {
                var ui = new FirstRunSetupUI(
                    s.GetRequiredService<Microsoft.Extensions.Logging.ILogger>(),
                    s.GetRequiredService<ShrinkUConfigService>());
                ui.OnCompleted = () =>
                {
                    try { s.GetRequiredService<ConversionUI>().IsOpen = true; } catch { }
                };
                return ui;
            });
            collection.AddSingleton<ShrinkUHostService>();

            // add sphene related singletons
            collection.AddSingleton<SpheneMediator>();
            collection.AddSingleton<FileCacheManager>();
            collection.AddSingleton<ServerConfigurationManager>();
            collection.AddSingleton<ApiController>();
            collection.AddSingleton<PerformanceCollectorService>();
            collection.AddSingleton<HubFactory>();
            collection.AddSingleton<FileUploadManager>();
            collection.AddSingleton<FileTransferOrchestrator>();
            collection.AddSingleton<SphenePlugin>();
            collection.AddSingleton<SpheneProfileManager>();
            collection.AddSingleton<GameObjectHandlerFactory>();
            collection.AddSingleton<FileDownloadManagerFactory>();
            collection.AddSingleton<PairHandlerFactory>();
            collection.AddSingleton<PairFactory>();
            collection.AddSingleton<XivDataAnalyzer>();
            collection.AddSingleton<CharacterAnalyzer>();
            collection.AddSingleton<TokenProvider>();
            collection.AddSingleton<PluginWarningNotificationService>();
            collection.AddSingleton<UpdateCheckService>();
            collection.AddSingleton<LoginHandler>();
            collection.AddSingleton<FileCompactor>();
            collection.AddSingleton<TagHandler>();
            collection.AddSingleton<IdDisplayHandler>();
            collection.AddSingleton<PlayerPerformanceService>();
            collection.AddSingleton<TransientResourceManager>();

            collection.AddSingleton<CharaDataManager>();
            collection.AddSingleton<CharaDataFileHandler>();
            collection.AddSingleton<CharaDataCharacterHandler>();
            collection.AddSingleton<CharaDataNearbyManager>();
            collection.AddSingleton<CharaDataGposeTogetherManager>();
            collection.AddSingleton<TextureBackupService>();
            collection.AddSingleton(s => new HousingOwnershipService(
                s.GetRequiredService<ILogger<HousingOwnershipService>>(),
                s.GetRequiredService<DalamudUtilService>(),
                s.GetRequiredService<SpheneConfigService>(),
                s.GetRequiredService<ApiController>(),
                condition));

            collection.AddSingleton(s => new VfxSpawnManager(s.GetRequiredService<ILogger<VfxSpawnManager>>(),
                gameInteropProvider, s.GetRequiredService<SpheneMediator>()));
            collection.AddSingleton((s) => new BlockedCharacterHandler(s.GetRequiredService<ILogger<BlockedCharacterHandler>>(), gameInteropProvider));
            collection.AddSingleton((s) => new IpcProvider(s.GetRequiredService<ILogger<IpcProvider>>(),
                pluginInterface,
                s.GetRequiredService<CharaDataManager>(),
                s.GetRequiredService<SpheneMediator>()));
            collection.AddSingleton<SelectPairForTagUi>();
            collection.AddSingleton((s) => new EventAggregator(pluginInterface.ConfigDirectory.FullName,
                s.GetRequiredService<ILogger<EventAggregator>>(), s.GetRequiredService<SpheneMediator>()));
            collection.AddSingleton((s) => new DalamudUtilService(s.GetRequiredService<ILogger<DalamudUtilService>>(),
                clientState, objectTable, framework, gameGui, condition, gameData, targetManager, gameConfig, partyList,
                s.GetRequiredService<BlockedCharacterHandler>(), s.GetRequiredService<SpheneMediator>(), s.GetRequiredService<PerformanceCollectorService>(),
                s.GetRequiredService<SpheneConfigService>()));
            collection.AddSingleton((s) => new CharacterStatusService(s.GetRequiredService<ILogger<CharacterStatusService>>(),
                clientState, condition, gameData, s.GetRequiredService<SpheneMediator>()));
            collection.AddSingleton((s) => new DtrEntry(s.GetRequiredService<ILogger<DtrEntry>>(), dtrBar, s.GetRequiredService<SpheneConfigService>(),
                s.GetRequiredService<SpheneMediator>(), s.GetRequiredService<PairManager>(), s.GetRequiredService<ApiController>()));
            collection.AddSingleton<Lazy<ApiController>>(s => new Lazy<ApiController>(() => s.GetRequiredService<ApiController>()));
            collection.AddSingleton<Lazy<PairManager>>(s => new Lazy<PairManager>(() => s.GetRequiredService<PairManager>()));
            collection.AddSingleton<Lazy<AreaBoundSyncshellService>>(s => new Lazy<AreaBoundSyncshellService>(() => s.GetRequiredService<AreaBoundSyncshellService>()));
            collection.AddSingleton(s => new AcknowledgmentTimeoutManager(s.GetRequiredService<ILogger<AcknowledgmentTimeoutManager>>(),
                s.GetRequiredService<SpheneMediator>(), s.GetRequiredService<Lazy<ApiController>>(), s.GetRequiredService<Lazy<PairManager>>()));
            collection.AddSingleton(s => new PairManager(s.GetRequiredService<ILogger<PairManager>>(), s.GetRequiredService<PairFactory>(),
                s.GetRequiredService<SpheneConfigService>(), s.GetRequiredService<SpheneMediator>(), contextMenu, s.GetRequiredService<ServerConfigurationManager>(),
                s.GetRequiredService<Lazy<ApiController>>(), s.GetRequiredService<SessionAcknowledgmentManager>(), s.GetRequiredService<MessageService>(),
                s.GetRequiredService<AcknowledgmentTimeoutManager>(), s.GetRequiredService<Lazy<AreaBoundSyncshellService>>()));
            collection.AddSingleton(s => new EnhancedAcknowledgmentManager(s.GetRequiredService<ILogger<EnhancedAcknowledgmentManager>>(),
                s.GetRequiredService<Lazy<ApiController>>().Value, s.GetRequiredService<SpheneMediator>(), new AcknowledgmentConfiguration(),
                s.GetRequiredService<SessionAcknowledgmentManager>()));
            collection.AddSingleton<RedrawManager>();
            collection.AddSingleton((s) => new IpcCallerPenumbra(s.GetRequiredService<ILogger<IpcCallerPenumbra>>(), pluginInterface,
                s.GetRequiredService<DalamudUtilService>(), s.GetRequiredService<SpheneMediator>(), s.GetRequiredService<RedrawManager>()));
            collection.AddSingleton((s) => new IpcCallerGlamourer(s.GetRequiredService<ILogger<IpcCallerGlamourer>>(), pluginInterface,
                s.GetRequiredService<DalamudUtilService>(), s.GetRequiredService<SpheneMediator>(), s.GetRequiredService<RedrawManager>()));
            collection.AddSingleton((s) => new IpcCallerCustomize(s.GetRequiredService<ILogger<IpcCallerCustomize>>(), pluginInterface,
                s.GetRequiredService<DalamudUtilService>(), s.GetRequiredService<SpheneMediator>()));
            collection.AddSingleton((s) => new IpcCallerHeels(s.GetRequiredService<ILogger<IpcCallerHeels>>(), pluginInterface,
                s.GetRequiredService<DalamudUtilService>(), s.GetRequiredService<SpheneMediator>()));
            collection.AddSingleton((s) => new IpcCallerHonorific(s.GetRequiredService<ILogger<IpcCallerHonorific>>(), pluginInterface,
                s.GetRequiredService<DalamudUtilService>(), s.GetRequiredService<SpheneMediator>()));
            collection.AddSingleton((s) => new IpcCallerMoodles(s.GetRequiredService<ILogger<IpcCallerMoodles>>(), pluginInterface,
                s.GetRequiredService<DalamudUtilService>(), s.GetRequiredService<SpheneMediator>()));
            collection.AddSingleton((s) => new IpcCallerPetNames(s.GetRequiredService<ILogger<IpcCallerPetNames>>(), pluginInterface,
                s.GetRequiredService<DalamudUtilService>(), s.GetRequiredService<SpheneMediator>()));
            collection.AddSingleton((s) => new IpcCallerBrio(s.GetRequiredService<ILogger<IpcCallerBrio>>(), pluginInterface,
                s.GetRequiredService<DalamudUtilService>()));
            collection.AddSingleton((s) => new IpcManager(s.GetRequiredService<ILogger<IpcManager>>(),
                s.GetRequiredService<SpheneMediator>(), s.GetRequiredService<IpcCallerPenumbra>(), s.GetRequiredService<IpcCallerGlamourer>(),
                s.GetRequiredService<IpcCallerCustomize>(), s.GetRequiredService<IpcCallerHeels>(), s.GetRequiredService<IpcCallerHonorific>(),
                s.GetRequiredService<IpcCallerMoodles>(), s.GetRequiredService<IpcCallerPetNames>(), s.GetRequiredService<IpcCallerBrio>()));
            collection.AddSingleton((s) => new MessageService(s.GetRequiredService<ILogger<MessageService>>(), notificationManager, s.GetRequiredService<SpheneConfigService>(), s.GetRequiredService<SpheneMediator>()));
            collection.AddSingleton((s) => new AcknowledgmentBatchingService(
                s.GetRequiredService<ILogger<AcknowledgmentBatchingService>>(),
                s.GetRequiredService<SpheneMediator>(),
                s.GetRequiredService<MessageService>()));
            collection.AddSingleton((s) => new NotificationService(s.GetRequiredService<ILogger<NotificationService>>(),
                s.GetRequiredService<SpheneMediator>(), s.GetRequiredService<DalamudUtilService>(),
                notificationManager, chatGui, s.GetRequiredService<SpheneConfigService>()));
            collection.AddSingleton((s) =>
            {
                var httpClient = new HttpClient();
                var ver = Assembly.GetExecutingAssembly().GetName().Version;
                httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Sphene", ver!.Major + "." + ver!.Minor + "." + ver!.Build));
                return httpClient;
            });
            collection.AddSingleton((s) => new SpheneConfigService(pluginInterface.ConfigDirectory.FullName));
            collection.AddSingleton((s) => new ServerConfigService(pluginInterface.ConfigDirectory.FullName));
            collection.AddSingleton((s) => new NotesConfigService(pluginInterface.ConfigDirectory.FullName));
            collection.AddSingleton((s) => new ServerTagConfigService(pluginInterface.ConfigDirectory.FullName));
            collection.AddSingleton((s) => new TransientConfigService(pluginInterface.ConfigDirectory.FullName));
            collection.AddSingleton((s) => new XivDataStorageService(pluginInterface.ConfigDirectory.FullName));
            collection.AddSingleton((s) => new PlayerPerformanceConfigService(pluginInterface.ConfigDirectory.FullName));
            collection.AddSingleton((s) => new CharaDataConfigService(pluginInterface.ConfigDirectory.FullName));
            collection.AddSingleton<AreaBoundSyncshellService>();
            collection.AddSingleton<CitySyncshellService>();
            collection.AddSingleton<IConfigService<ISpheneConfiguration>>(s => s.GetRequiredService<SpheneConfigService>());
            collection.AddSingleton<IConfigService<ISpheneConfiguration>>(s => s.GetRequiredService<ServerConfigService>());
            collection.AddSingleton<IConfigService<ISpheneConfiguration>>(s => s.GetRequiredService<NotesConfigService>());
            collection.AddSingleton<IConfigService<ISpheneConfiguration>>(s => s.GetRequiredService<ServerTagConfigService>());
            collection.AddSingleton<IConfigService<ISpheneConfiguration>>(s => s.GetRequiredService<TransientConfigService>());
            collection.AddSingleton<IConfigService<ISpheneConfiguration>>(s => s.GetRequiredService<XivDataStorageService>());
            collection.AddSingleton<IConfigService<ISpheneConfiguration>>(s => s.GetRequiredService<PlayerPerformanceConfigService>());
            collection.AddSingleton<IConfigService<ISpheneConfiguration>>(s => s.GetRequiredService<CharaDataConfigService>());
            collection.AddSingleton<ConfigurationMigrator>();
            collection.AddSingleton<ConfigurationSaveService>();

            collection.AddSingleton<HubFactory>();
            
            // Add reliability services
            collection.AddSingleton<ConnectionHealthMonitor>();
            collection.AddSingleton<CircuitBreakerService>();

            // add scoped services
            collection.AddScoped<DrawEntityFactory>();
            collection.AddScoped<CacheMonitor>();
            collection.AddScoped<UiFactory>();
            collection.AddScoped<SelectTagForPairUi>();
            collection.AddScoped<WindowMediatorSubscriberBase, SettingsUi>();
            collection.AddScoped<WindowMediatorSubscriberBase, CompactUi>();
            collection.AddScoped<WindowMediatorSubscriberBase, SpheneIcon>();
            collection.AddScoped<WindowMediatorSubscriberBase, IntroUi>();
            collection.AddScoped<WindowMediatorSubscriberBase, DownloadUi>();
            collection.AddScoped<WindowMediatorSubscriberBase, PopoutProfileUi>();
            collection.AddScoped<WindowMediatorSubscriberBase, DataAnalysisUi>();
            collection.AddScoped<WindowMediatorSubscriberBase, JoinSyncshellUI>();
            collection.AddScoped<WindowMediatorSubscriberBase, CreateSyncshellUI>();
            collection.AddScoped<WindowMediatorSubscriberBase, EventViewerUI>();
            collection.AddScoped<WindowMediatorSubscriberBase, CharaDataHubUi>();
            collection.AddScoped<WindowMediatorSubscriberBase, StatusDebugUi>();

            collection.AddScoped<WindowMediatorSubscriberBase, AreaBoundSyncshellConsentUI>();
            collection.AddScoped<WindowMediatorSubscriberBase, AreaBoundSyncshellSelectionUI>();
            collection.AddSingleton<WindowMediatorSubscriberBase, CitySyncshellExplanationUI>((s) =>
            {
                var logger = s.GetRequiredService<ILogger<CitySyncshellExplanationUI>>();
                logger.LogDebug("CitySyncshellExplanationUI factory method called - creating instance");
                var mediator = s.GetRequiredService<SpheneMediator>();
                var performanceService = s.GetRequiredService<PerformanceCollectorService>();
                var configService = s.GetRequiredService<SpheneConfigService>();
                var areaBoundSyncshellService = s.GetRequiredService<AreaBoundSyncshellService>();
                var instance = new CitySyncshellExplanationUI(logger, mediator, performanceService, configService, areaBoundSyncshellService);
                logger.LogDebug("CitySyncshellExplanationUI factory method completed - instance created");
                return instance;
            });
            collection.AddScoped<WindowMediatorSubscriberBase, WelcomePageLivePreviewUI>();
            collection.AddSingleton<WindowMediatorSubscriberBase, UpdateNotificationUi>((s) => new UpdateNotificationUi(
                s.GetRequiredService<ILogger<UpdateNotificationUi>>(),
                s.GetRequiredService<UiSharedService>(),
                s.GetRequiredService<SpheneMediator>(),
                s.GetRequiredService<PerformanceCollectorService>(),
                s.GetRequiredService<ICommandManager>()));
            collection.AddSingleton<WindowMediatorSubscriberBase, ReleaseChangelogUi>((s) => new ReleaseChangelogUi(
                s.GetRequiredService<ILogger<ReleaseChangelogUi>>(),
                s.GetRequiredService<UiSharedService>(),
                s.GetRequiredService<SpheneConfigService>(),
                s.GetRequiredService<SpheneMediator>(),
                s.GetRequiredService<PerformanceCollectorService>(),
                s.GetRequiredService<ChangelogService>()));

            collection.AddScoped<WindowMediatorSubscriberBase, EditProfileUi>((s) => new EditProfileUi(s.GetRequiredService<ILogger<EditProfileUi>>(),
                s.GetRequiredService<SpheneMediator>(), s.GetRequiredService<ApiController>(), s.GetRequiredService<UiSharedService>(), s.GetRequiredService<FileDialogManager>(),
                s.GetRequiredService<SpheneProfileManager>(), s.GetRequiredService<PerformanceCollectorService>()));
            collection.AddScoped<WindowMediatorSubscriberBase, PopupHandler>();
            collection.AddScoped<IPopupHandler, BanUserPopupHandler>();
            collection.AddScoped<IPopupHandler, CensusPopupHandler>();
            collection.AddScoped<CacheCreationService>();
            collection.AddScoped<PlayerDataFactory>();
            collection.AddScoped<VisibleUserDataDistributor>();
            
            // Enhanced Acknowledgment System
            collection.AddSingleton<AcknowledgmentConfiguration>();
            collection.AddScoped<EnhancedAcknowledgmentManager>();
            collection.AddScoped<WindowMediatorSubscriberBase, AcknowledgmentMonitorUI>();
            collection.AddSingleton<SessionAcknowledgmentManager>(s => 
            {
                var lazyPairManager = new Lazy<PairManager>(() => s.GetRequiredService<PairManager>());
                return new SessionAcknowledgmentManager(
                    s.GetRequiredService<ILogger<SessionAcknowledgmentManager>>(),
                    s.GetRequiredService<SpheneMediator>(),
                    userData => lazyPairManager.Value.GetPairForUser(userData),
                    s.GetRequiredService<MessageService>(),
                    s.GetRequiredService<AcknowledgmentBatchingService>()
                );
            });
            collection.AddSingleton<IconUpdateService>();
            collection.AddSingleton<HalloweenEasterEggService>();
            
            collection.AddScoped((s) => new UiService(s.GetRequiredService<ILogger<UiService>>(), pluginInterface.UiBuilder, s.GetRequiredService<SpheneConfigService>(),
                s.GetRequiredService<WindowSystem>(), s.GetServices<WindowMediatorSubscriberBase>(),
                s.GetRequiredService<UiFactory>(),
                s.GetRequiredService<FileDialogManager>(), s.GetRequiredService<SpheneMediator>()));
            collection.AddScoped((s) => new CommandManagerService(commandManager, s.GetRequiredService<PerformanceCollectorService>(),
                s.GetRequiredService<ServerConfigurationManager>(), s.GetRequiredService<CacheMonitor>(), s.GetRequiredService<ApiController>(),
                s.GetRequiredService<SpheneMediator>(), s.GetRequiredService<SpheneConfigService>()));
            collection.AddScoped((s) => new UiSharedService(s.GetRequiredService<ILogger<UiSharedService>>(), s.GetRequiredService<IpcManager>(), s.GetRequiredService<ApiController>(),
                s.GetRequiredService<CacheMonitor>(), s.GetRequiredService<FileDialogManager>(), s.GetRequiredService<SpheneConfigService>(), s.GetRequiredService<DalamudUtilService>(),
                pluginInterface, textureProvider, s.GetRequiredService<Dalamud.Localization>(), s.GetRequiredService<ServerConfigurationManager>(), s.GetRequiredService<TokenProvider>(),
                s.GetRequiredService<SpheneMediator>()));

            collection.AddHostedService(p => p.GetRequiredService<ConfigurationSaveService>());
            collection.AddHostedService(p => p.GetRequiredService<SpheneMediator>());
            collection.AddHostedService(p => p.GetRequiredService<NotificationService>());
            collection.AddHostedService(p => p.GetRequiredService<FileCacheManager>());
            collection.AddHostedService(p => p.GetRequiredService<ConfigurationMigrator>());
            collection.AddHostedService(p => p.GetRequiredService<DalamudUtilService>());
            collection.AddHostedService(p => p.GetRequiredService<CharacterStatusService>());
            collection.AddHostedService(p => p.GetRequiredService<PerformanceCollectorService>());
            collection.AddHostedService(p => p.GetRequiredService<DtrEntry>());
            collection.AddHostedService(p => p.GetRequiredService<EventAggregator>());
            collection.AddHostedService(p => p.GetRequiredService<IpcProvider>());
            collection.AddHostedService(p => p.GetRequiredService<LoginHandler>());
            collection.AddHostedService(p => p.GetRequiredService<UpdateCheckService>());
            collection.AddSingleton<ChangelogService>();
            collection.AddSingleton<ReleaseChangelogStartupService>();
            collection.AddHostedService(p => p.GetRequiredService<ReleaseChangelogStartupService>());
            collection.AddHostedService(p => p.GetRequiredService<ShrinkUHostService>());
            // Initialize CitySyncshellExplanationUI early as hosted service to ensure it's created before CitySyncshellService
            collection.AddHostedService(p => 
            {
                // Force creation of CitySyncshellExplanationUI singleton - get it as WindowMediatorSubscriberBase
                var windows = p.GetServices<WindowMediatorSubscriberBase>();
                var citySyncshellUI = windows.OfType<CitySyncshellExplanationUI>().FirstOrDefault();
                return new DummyHostedService();
            });
            collection.AddHostedService(p => p.GetRequiredService<AreaBoundSyncshellService>());
            collection.AddHostedService(p => p.GetRequiredService<CitySyncshellService>());
            collection.AddHostedService(p => p.GetRequiredService<HalloweenEasterEggService>());
            collection.AddHostedService(p => p.GetRequiredService<SphenePlugin>());
        })
        .Build();

        _ = _host.StartAsync();
    }

    public void Dispose()
    {
        _host.StopAsync().GetAwaiter().GetResult();
        _host.Dispose();
    }
}
