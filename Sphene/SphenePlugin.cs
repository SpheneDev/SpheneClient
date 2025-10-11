using Sphene.FileCache;
using Sphene.SpheneConfiguration;
using Sphene.PlayerData.Pairs;
using Sphene.PlayerData.Services;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.Services.ServerConfiguration;
using Sphene.UI;
using Sphene.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Reflection;

namespace Sphene;

#pragma warning disable S125 // Sections of code should not be commented out
/*
                                                                    (..,,...,,,,,+/,                ,,.....,,+
                                                              ..,,+++/((###%%%&&%%#(+,,.,,,+++,,,,//,,#&@@@@%+.
                                                          ...+//////////(/,,,,++,.,(###((//////////,..  .,#@@%/./
                                                       ,..+/////////+///,.,. ,&@@@@,,/////////////+,..    ,(##+,.
                                                    ,,.+//////////++++++..     ./#%#,+/////////////+,....,/((,..,
                                                  +..////////////+++++++...  .../##(,,////////////////++,,,+/(((+,
                                                +,.+//////////////+++++++,.,,,/(((+.,////////////////////////((((#/,,
                                              /+.+//////////++++/++++++++++,,...,++///////////////////////////((((##,
                                             /,.////////+++++++++++++++++++++////////+++//////++/+++++//////////((((#(+,
                                           /+.+////////+++++++++++++++++++++++++++++++++++++++++++++++++++++/////((((##+
                                          +,.///////////////+++++++++++++++++++++++++++++++++++++++++++++++++++///((((%/
                                         /.,/////////////////+++++++++++++++++++++++++++++++++++++++++++++++++++///+/(#+
                                        +,./////////////////+++++++++++++++++++++++++++++++++++++++++++++++,,+++++///((,
                                       ...////////++/++++++++++++++++++++++++,,++++++++++++++++++++++++++++++++++++//(,,
                                       ..//+,+///++++++++++++++++++,,,,+++,,,,,,,,,,,,++++++++,,+++++++++++++++++++//,,+
                                      ..,++,.++++++++++++++++++++++,,,,,,,,,,,,,,,,,,,++++++++,,,,,,,,,,++++++++++...
                                      ..+++,.+++++++++++++++++++,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,++,..,.
                                     ..,++++,,+++++++++++,+,,,,,,,,,,..,+++++++++,,,,,,.....................,//+,+
                                 ....,+++++,.,+++++++++++,,,,,,,,.+///(((((((((((((///////////////////////(((+,,,
                          .....,++++++++++..,+++++++++++,,.,,,.////////(((((((((((((((////////////////////+,,/
                      .....,++++++++++++,..,,+++++++++,,.,../////////////////((((((((((//////////////////,,+
                   ...,,+++++++++++++,.,,.,,,+++++++++,.,/////////////////(((//++++++++++++++//+++++++++/,,
                ....,++++++++++++++,.,++.,++++++++++++.,+////////////////////+++++++++++++++++++++++++///,,..
              ...,++++++++++++++++..+++..+++++++++++++.,//////////////////////////++++++++++++///////++++......
            ...++++++++++++++++++..++++.,++,++++++++++.+///////////////////////////////////////////++++++..,,,..
          ...+++++++++++++++++++..+++++..,+,,+++++++++.+//////////////////////////////////////////+++++++...,,,,..
         ..++++++++++++++++++++..++++++..,+,,+++++++++.+//////////////////////////////////////++++++++++,....,,,,..
       ...+++//(//////+++++++++..++++++,.,+++++++++++++,..,....,,,+++///////////////////////++++++++++++..,,,,,,,,...
      ..,++/(((((//////+++++++,.,++++++,,.,,,+++++++++++++++++++++++,.++////////////////////+++++++++++.....,,,,,,,...
     ..,//#(((((///////+++++++..++++++++++,...,++,++++++++++++++++,...+++/////////////////////+,,,+++...  ....,,,,,,...
   ...+//(((((//////////++++++..+++++++++++++++,......,,,,++++++,,,..+++////////////////////////+,....     ...,,,,,,,...
   ..,//((((////////////++++++..++++++/+++++++++++++,,...,,........,+/+//////////////////////((((/+,..     ....,.,,,,..
  ...+/////////////////////+++..++++++/+///+++++++++++++++++++++///+/+////////////////////////(((((/+...   .......,,...
  ..++////+++//////////////++++.+++++++++///////++++++++////////////////////////////////////+++/(((((/+..    .....,,...
  .,++++++++///////////////++++..++++//////////////////////////////////////////////////////++++++/((((++..    ........
  .+++++++++////////////////++++,.+++/////////////////////////////////////////////////////+++++++++/((/++..
 .,++++++++//////////////////++++,.+++//////////////////////////////////////////////////+++++++++++++//+++..
 .++++++++//////////////////////+/,.,+++////((((////////////////////////////////////////++++++++++++++++++...
 .++++++++///////////////////////+++..++++//((((((((///////////////////////////////////++++++++++++++++++++ .
 .++++++///////////////////////////++,.,+++++/(((((((((/////////////////////////////+++++++++++++++++++++++,..
 .++++++////////////////////////////+++,.,+++++++/((((((((//////////////////////////++++++++++++++++++++++++..
 .+++++++///////////////////++////////++++,.,+++++++++///////////+////////////////+++++++++++++++++++++++++,..
 ..++++++++++//////////////////////+++++++..+...,+++++++++++++++/++++++++++++++++++++++++++++++++++++++++++,...
  ..++++++++++++///////////////+++++++,...,,,,,.,....,,,,+++++++++++++++++++++++++++++++++++++++++++++++,,,,...
  ...++++++++++++++++++++++++++,,,,...,,,,,,,,,..,,++,,,.,,,,,,,,,,,,,,,,,,+++++++++++++++++++++++++,,,,,,,,..
   ...+++++++++++++++,,,,,,,,....,,,,,,,,,,,,,,,..,,++++++,,,,,,,,,,,,,,,,+++++++++++++++++++++++++,,,,,,,,,..
     ...++++++++++++,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,...,++++++++++++++++++++++++++++++++++++++++++++,,,,,,,,,,...
       ,....,++++++++++++++,,,+++++++,,,,,,,,,,,,,,,,,.,++++++++++++++++++++++++++++++++++++++++++++,,,,,,,,..

*/
#pragma warning restore S125 // Sections of code should not be commented out

public class SphenePlugin : MediatorSubscriberBase, IHostedService
{
    private readonly DalamudUtilService _dalamudUtil;
    private readonly SpheneConfigService _SpheneConfigService;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private IServiceScope? _runtimeServiceScope;
    private Task? _launchTask = null;

    public SphenePlugin(ILogger<SphenePlugin> logger, SpheneConfigService SpheneConfigService,
        ServerConfigurationManager serverConfigurationManager,
        DalamudUtilService dalamudUtil,
        IServiceScopeFactory serviceScopeFactory, SpheneMediator mediator) : base(logger, mediator)
    {
        _SpheneConfigService = SpheneConfigService;
        _serverConfigurationManager = serverConfigurationManager;
        _dalamudUtil = dalamudUtil;
        _serviceScopeFactory = serviceScopeFactory;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version!;
        Logger.LogInformation("Launching {name} {major}.{minor}.{build}", "Sphene", version.Major, version.Minor, version.Build);
        Mediator.Publish(new EventMessage(new Services.Events.Event(nameof(SphenePlugin), Services.Events.EventSeverity.Informational,
            $"Starting Sphene {version.Major}.{version.Minor}.{version.Build}")));

        // Initialize ThemeManager with configuration service
        ThemeManager.Initialize(Logger, _SpheneConfigService.ConfigurationDirectory, _SpheneConfigService);
        // Load saved theme on startup if auto-load is enabled
        if (ThemeManager.ShouldAutoLoadTheme())
        {
            var selectedThemeName = ThemeManager.GetSelectedTheme();
            Logger.LogDebug($"Auto-loading theme: {selectedThemeName}");
            
            // Try to load the selected theme
            if (selectedThemeName == "Default Sphene" || ThemePresets.BuiltInThemes.ContainsKey(selectedThemeName))
            {
                // Load built-in theme
                var presetTheme = ThemePresets.BuiltInThemes.GetValueOrDefault(selectedThemeName, ThemePresets.BuiltInThemes["Default Sphene"]);
                CopyThemeProperties(presetTheme, SpheneCustomTheme.CurrentTheme);
                Logger.LogDebug($"Loaded built-in theme: {selectedThemeName}");
            }
            else
            {
                // Try to load custom theme
                var customTheme = ThemeManager.LoadTheme(selectedThemeName);
                if (customTheme != null)
                {
                    CopyThemeProperties(customTheme, SpheneCustomTheme.CurrentTheme);
                    Logger.LogDebug($"Loaded custom theme: {selectedThemeName}");
                }
                else
                {
                    Logger.LogWarning($"Failed to load theme '{selectedThemeName}', falling back to Default Sphene");
                    var defaultTheme = ThemePresets.BuiltInThemes["Default Sphene"];
                    CopyThemeProperties(defaultTheme, SpheneCustomTheme.CurrentTheme);
                }
            }
            
            // Notify theme changed to apply immediately
            SpheneCustomTheme.CurrentTheme.NotifyThemeChanged();
            
            // Add a small delay to ensure UI components have time to apply the theme
            Task.Delay(100).ContinueWith(_ => 
            {
                // Force another theme change notification to ensure all UI components are updated
                SpheneCustomTheme.CurrentTheme.NotifyThemeChanged();
            });
        }

        Mediator.Subscribe<SwitchToMainUiMessage>(this, (msg) => { if (_launchTask == null || _launchTask.IsCompleted) _launchTask = Task.Run(WaitForPlayerAndLaunchCharacterManager); });
        Mediator.Subscribe<DalamudLoginMessage>(this, (_) => DalamudUtilOnLogIn());
        Mediator.Subscribe<DalamudLogoutMessage>(this, (_) => DalamudUtilOnLogOut());

        Mediator.StartQueueProcessing();

        return Task.CompletedTask;
    }
    
    private static void CopyThemeProperties(ThemeConfiguration source, ThemeConfiguration target)
    {
        // Use the built-in Clone method to ensure all properties are copied
        var cloned = source.Clone();
        
        // Copy all properties from cloned to target
        target.WindowRounding = cloned.WindowRounding;
        target.ChildRounding = cloned.ChildRounding;
        target.PopupRounding = cloned.PopupRounding;
        target.FrameRounding = cloned.FrameRounding;
        target.ScrollbarRounding = cloned.ScrollbarRounding;
        target.GrabRounding = cloned.GrabRounding;
        target.TabRounding = cloned.TabRounding;
        target.CompactWindowRounding = cloned.CompactWindowRounding;
        target.CompactChildRounding = cloned.CompactChildRounding;
        target.CompactPopupRounding = cloned.CompactPopupRounding;
        target.CompactFrameRounding = cloned.CompactFrameRounding;
        target.CompactScrollbarRounding = cloned.CompactScrollbarRounding;
        target.CompactGrabRounding = cloned.CompactGrabRounding;
        target.CompactTabRounding = cloned.CompactTabRounding;
        target.CompactHeaderRounding = cloned.CompactHeaderRounding;
        
        // Spacing Settings
        target.WindowPadding = cloned.WindowPadding;
        target.FramePadding = cloned.FramePadding;
        target.ItemSpacing = cloned.ItemSpacing;
        target.ItemInnerSpacing = cloned.ItemInnerSpacing;
        target.IndentSpacing = cloned.IndentSpacing;
        target.CompactWindowPadding = cloned.CompactWindowPadding;
        target.CompactFramePadding = cloned.CompactFramePadding;
        target.CompactItemSpacing = cloned.CompactItemSpacing;
        target.CompactItemInnerSpacing = cloned.CompactItemInnerSpacing;
        target.CompactCellPadding = cloned.CompactCellPadding;
        target.CompactChildPadding = cloned.CompactChildPadding;
        target.CompactIndentSpacing = cloned.CompactIndentSpacing;
        target.CompactScrollbarSize = cloned.CompactScrollbarSize;
        target.CompactGrabMinSize = cloned.CompactGrabMinSize;
        target.CompactButtonTextAlign = cloned.CompactButtonTextAlign;
        target.CompactSelectableTextAlign = cloned.CompactSelectableTextAlign;
        
        // Border Settings
        target.WindowBorderSize = cloned.WindowBorderSize;
        target.ChildBorderSize = cloned.ChildBorderSize;
        target.PopupBorderSize = cloned.PopupBorderSize;
        target.FrameBorderSize = cloned.FrameBorderSize;
        target.CompactWindowBorderSize = cloned.CompactWindowBorderSize;
        target.CompactChildBorderSize = cloned.CompactChildBorderSize;
        target.CompactPopupBorderSize = cloned.CompactPopupBorderSize;
        target.CompactFrameBorderSize = cloned.CompactFrameBorderSize;
        target.CompactTooltipRounding = cloned.CompactTooltipRounding;
        target.CompactTooltipBorderSize = cloned.CompactTooltipBorderSize;
        target.CompactContextMenuRounding = cloned.CompactContextMenuRounding;
        target.CompactContextMenuBorderSize = cloned.CompactContextMenuBorderSize;
        target.ScrollbarSize = cloned.ScrollbarSize;
        target.GrabMinSize = cloned.GrabMinSize;
        
        // Color Properties
        target.PrimaryDark = cloned.PrimaryDark;
        target.SecondaryDark = cloned.SecondaryDark;
        target.AccentBlue = cloned.AccentBlue;
        target.AccentCyan = cloned.AccentCyan;
        target.TextPrimary = cloned.TextPrimary;
        target.TextSecondary = cloned.TextSecondary;
        target.Border = cloned.Border;
        target.Hover = cloned.Hover;
        target.Active = cloned.Active;
        target.HeaderBg = cloned.HeaderBg;
        
        // Window Colors
        target.WindowBg = cloned.WindowBg;
        target.ChildBg = cloned.ChildBg;
        target.PopupBg = cloned.PopupBg;
        target.BorderShadow = cloned.BorderShadow;
        
        // Frame Colors
        target.FrameBg = cloned.FrameBg;
        target.FrameBgHovered = cloned.FrameBgHovered;
        target.FrameBgActive = cloned.FrameBgActive;
        
        // Title Bar Colors
        target.TitleBg = cloned.TitleBg;
        target.TitleBgActive = cloned.TitleBgActive;
        target.TitleBgCollapsed = cloned.TitleBgCollapsed;
        
        // Menu Colors
        target.MenuBarBg = cloned.MenuBarBg;
        
        // Scrollbar Colors
        target.ScrollbarBg = cloned.ScrollbarBg;
        target.ScrollbarGrab = cloned.ScrollbarGrab;
        target.ScrollbarGrabHovered = cloned.ScrollbarGrabHovered;
        target.ScrollbarGrabActive = cloned.ScrollbarGrabActive;
        
        // Check Mark Colors
        target.CheckMark = cloned.CheckMark;
        
        // Slider Colors
        target.SliderGrab = cloned.SliderGrab;
        target.SliderGrabActive = cloned.SliderGrabActive;
        
        // Button Colors
        target.Button = cloned.Button;
        target.ButtonHovered = cloned.ButtonHovered;
        target.ButtonActive = cloned.ButtonActive;
        
        // Header Colors
        target.Header = cloned.Header;
        target.HeaderHovered = cloned.HeaderHovered;
        target.HeaderActive = cloned.HeaderActive;
        
        // Separator Colors
        target.Separator = cloned.Separator;
        target.SeparatorHovered = cloned.SeparatorHovered;
        target.SeparatorActive = cloned.SeparatorActive;
        
        // Resize Grip Colors
        target.ResizeGrip = cloned.ResizeGrip;
        target.ResizeGripHovered = cloned.ResizeGripHovered;
        target.ResizeGripActive = cloned.ResizeGripActive;
        
        // Tab Colors
        target.Tab = cloned.Tab;
        target.TabHovered = cloned.TabHovered;
        target.TabActive = cloned.TabActive;
        target.TabUnfocused = cloned.TabUnfocused;
        target.TabUnfocusedActive = cloned.TabUnfocusedActive;
        
        // Table Colors
        target.TableHeaderBg = cloned.TableHeaderBg;
        target.TableBorderStrong = cloned.TableBorderStrong;
        target.TableBorderLight = cloned.TableBorderLight;
        target.TableRowBg = cloned.TableRowBg;
        target.TableRowBgAlt = cloned.TableRowBgAlt;
        
        // Text Colors
        target.TextDisabled = cloned.TextDisabled;
        target.TextSelectedBg = cloned.TextSelectedBg;
        
        // Drag Drop Colors
        target.DragDropTarget = cloned.DragDropTarget;
        
        // Navigation Colors
        target.NavHighlight = cloned.NavHighlight;
        target.NavWindowingHighlight = cloned.NavWindowingHighlight;
        target.NavWindowingDimBg = cloned.NavWindowingDimBg;
        
        // Modal Colors
        target.ModalWindowDimBg = cloned.ModalWindowDimBg;
        
        // CompactUI Specific Colors
        target.CompactWindowBg = cloned.CompactWindowBg;
        target.CompactChildBg = cloned.CompactChildBg;
        target.CompactPopupBg = cloned.CompactPopupBg;
        target.CompactTitleBg = cloned.CompactTitleBg;
        target.CompactTitleBgActive = cloned.CompactTitleBgActive;
        target.CompactFrameBg = cloned.CompactFrameBg;
        target.CompactButton = cloned.CompactButton;
        target.CompactButtonHovered = cloned.CompactButtonHovered;
        target.CompactButtonActive = cloned.CompactButtonActive;
        target.CompactHeaderBg = cloned.CompactHeaderBg;
        target.CompactBorder = cloned.CompactBorder;
        target.CompactText = cloned.CompactText;
        target.CompactTextSecondary = cloned.CompactTextSecondary;
        target.CompactAccent = cloned.CompactAccent;
        target.CompactHover = cloned.CompactHover;
        target.CompactActive = cloned.CompactActive;
        target.CompactHeaderText = cloned.CompactHeaderText;
        target.CompactUidColor = cloned.CompactUidColor;
        target.CompactServerStatusConnected = cloned.CompactServerStatusConnected;
        target.CompactServerStatusWarning = cloned.CompactServerStatusWarning;
        target.CompactServerStatusError = cloned.CompactServerStatusError;
        target.CompactActionButton = cloned.CompactActionButton;
        target.CompactActionButtonHovered = cloned.CompactActionButtonHovered;
        target.CompactActionButtonActive = cloned.CompactActionButtonActive;
        target.CompactSyncshellButton = cloned.CompactSyncshellButton;
        target.CompactSyncshellButtonHovered = cloned.CompactSyncshellButtonHovered;
        target.CompactSyncshellButtonActive = cloned.CompactSyncshellButtonActive;
        target.CompactPanelTitleText = cloned.CompactPanelTitleText;
        target.CompactConnectedText = cloned.CompactConnectedText;
        target.CompactAllSyncshellsText = cloned.CompactAllSyncshellsText;
        target.CompactOfflinePausedText = cloned.CompactOfflinePausedText;
        target.CompactOfflineSyncshellText = cloned.CompactOfflineSyncshellText;
        target.CompactVisibleText = cloned.CompactVisibleText;
        target.CompactPairsText = cloned.CompactPairsText;
        target.CompactShowImGuiHeader = cloned.CompactShowImGuiHeader;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        UnsubscribeAll();

        DalamudUtilOnLogOut();

        Logger.LogDebug("Halting SphenePlugin");

        return Task.CompletedTask;
    }

    private void DalamudUtilOnLogIn()
    {
        Logger?.LogDebug("Client login");
        if (_launchTask == null || _launchTask.IsCompleted) _launchTask = Task.Run(WaitForPlayerAndLaunchCharacterManager);
    }

    private void DalamudUtilOnLogOut()
    {
        Logger?.LogDebug("Client logout");

        _runtimeServiceScope?.Dispose();
    }

    private async Task WaitForPlayerAndLaunchCharacterManager()
    {
        while (!await _dalamudUtil.GetIsPlayerPresentAsync().ConfigureAwait(false))
        {
            await Task.Delay(100).ConfigureAwait(false);
        }

        try
        {
            Logger?.LogDebug("Launching Managers");

            _runtimeServiceScope?.Dispose();
            _runtimeServiceScope = _serviceScopeFactory.CreateScope();
            _runtimeServiceScope.ServiceProvider.GetRequiredService<UiService>();
            _runtimeServiceScope.ServiceProvider.GetRequiredService<CommandManagerService>();
            if (!_SpheneConfigService.Current.HasValidSetup() || !_serverConfigurationManager.HasValidConfig())
            {
                Mediator.Publish(new SwitchToIntroUiMessage());
                return;
            }
            _runtimeServiceScope.ServiceProvider.GetRequiredService<CacheCreationService>();
            _runtimeServiceScope.ServiceProvider.GetRequiredService<TransientResourceManager>();
            _runtimeServiceScope.ServiceProvider.GetRequiredService<VisibleUserDataDistributor>();
            _runtimeServiceScope.ServiceProvider.GetRequiredService<NotificationService>();

#if !DEBUG
            if (_SpheneConfigService.Current.LogLevel != LogLevel.Information)
            {
                Mediator.Publish(new NotificationMessage("Abnormal Log Level",
                    $"Your log level is set to '{_SpheneConfigService.Current.LogLevel}' which is not recommended for normal usage. Set it to '{LogLevel.Information}' in \"Sphene Settings -> Debug\" unless instructed otherwise.",
                    SpheneConfiguration.Models.NotificationType.Error, TimeSpan.FromSeconds(15000)));
            }
#endif
        }
        catch (Exception ex)
        {
            Logger?.LogCritical(ex, "Error during launch of managers");
        }
    }
}
