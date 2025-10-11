using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.UI.Styling;
using Sphene.SpheneConfiguration;
using Sphene.WebAPI;
using Sphene.WebAPI.SignalR.Utils;
using System.Numerics;
using Dalamud.Interface.Textures.TextureWraps;
using System;
using Sphene.SpheneConfiguration.Models;
using Sphene.Interop.Ipc;
using Dalamud.Plugin;

namespace Sphene.UI;

public class SpheneIcon : WindowMediatorSubscriberBase, IDisposable
{
    private readonly ILogger<SpheneIcon> _logger;
    private readonly SpheneMediator _mediator;
    private readonly SpheneConfigService _configService;
    private readonly UiSharedService _uiSharedService;
    private readonly ApiController _apiController;
    private readonly IpcManager _ipcManager;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ICommandManager _commandManager;
    
    private Vector2 _iconPosition = new Vector2(100, 100);
    private bool _hasStoredIconPosition = false;
    private bool _isDragging = false;
    private bool _wasClicked = false;
    private Vector2 _clickStartPos;
    private IDalamudTextureWrap? _spheneLogoTexture;
    
    // Context menu state
    // Removed _showContextMenu and _contextMenuPosition as they're no longer needed with BeginPopupContextItem
    
    // Update indicator state
    private bool _updateAvailable = false;
    private UpdateInfo? _updateInfo = null;
    private bool _updateToastShown = false;
    
    
    public SpheneIcon(ILogger<SpheneIcon> logger, SpheneMediator mediator, 
        SpheneConfigService configService, UiSharedService uiSharedService, ApiController apiController, 
        PerformanceCollectorService performanceCollectorService, IpcManager ipcManager, IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager) 
        : base(logger, mediator, "###SpheneIcon", performanceCollectorService)
    {
        _logger = logger;
        _mediator = mediator;
        _configService = configService;
        _uiSharedService = uiSharedService;
        _apiController = apiController;
        _ipcManager = ipcManager;
        _pluginInterface = pluginInterface;
        _commandManager = commandManager;
        
        LoadIconPositionFromConfig();
        
        // Load Sphene Logo Texture
        LoadSpheneLogoTexture();
        
        // Set window flags for a draggable icon
        Flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
                ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove;
        
        if (_hasStoredIconPosition)
        {
            Position = _iconPosition;
            PositionCondition = ImGuiCond.FirstUseEver;
        }
        
        // Show icon based on configuration setting
        IsOpen = _configService.Current.ShowSpheneIcon;
        
        // Subscribe to configuration changes
        _configService.ConfigSave += OnConfigurationChanged;
        
        // Subscribe to update availability messages
        Mediator.Subscribe<ShowUpdateNotificationMessage>(this, OnUpdateAvailable);
        
        _logger.LogDebug("SpheneIcon created at position {Position}", _iconPosition);
    }
    
    private void DrawContextMenu()
    {
        // Use BeginPopupContextItem for proper context menu behavior
        if (ImGui.BeginPopupContextItem("SpheneIconContextMenu"))
        {
            ImGui.Text("Quick Access Plugins");
            ImGui.Separator();
            
            // Check available plugins and show them
            var availablePlugins = GetAvailablePlugins();
            
            foreach (var plugin in availablePlugins)
            {
                if (ImGui.MenuItem($"Open {plugin.Name}"))
                {
                    OpenPlugin(plugin.InternalName);
                }
            }
            
            if (availablePlugins.Count == 0)
            {
                ImGui.TextDisabled("No compatible plugins found");
            }
            
            ImGui.Separator();
            
            if (ImGui.MenuItem("Settings"))
            {
                // Open Sphene settings
                _mediator.Publish(new UiToggleMessage(typeof(SettingsUi)));
            }
            
            ImGui.EndPopup();
        }
    }
    
    private List<PluginInfo> GetAvailablePlugins()
    {
        var compatiblePlugins = new List<PluginInfo>();
        
        // Check for Penumbra
        if (_ipcManager.Penumbra.APIAvailable)
        {
            var penumbraPlugin = _pluginInterface.InstalledPlugins
                .FirstOrDefault(p => string.Equals(p.InternalName, "Penumbra", StringComparison.OrdinalIgnoreCase));
            if (penumbraPlugin != null)
            {
                compatiblePlugins.Add(new PluginInfo("Penumbra", "Penumbra"));
            }
        }
        
        // Check for Glamourer
        if (_ipcManager.Glamourer.APIAvailable)
        {
            var glamourerPlugin = _pluginInterface.InstalledPlugins
                .FirstOrDefault(p => string.Equals(p.InternalName, "Glamourer", StringComparison.OrdinalIgnoreCase));
            if (glamourerPlugin != null)
            {
                compatiblePlugins.Add(new PluginInfo("Glamourer", "Glamourer"));
            }
        }
        
        // Check for other compatible plugins
        if (_ipcManager.CustomizePlus.APIAvailable)
        {
            var customizePlusPlugin = _pluginInterface.InstalledPlugins
                .FirstOrDefault(p => string.Equals(p.InternalName, "CustomizePlus", StringComparison.OrdinalIgnoreCase));
            if (customizePlusPlugin != null)
            {
                compatiblePlugins.Add(new PluginInfo("Customize+", "CustomizePlus"));
            }
        }
        
        if (_ipcManager.Heels.APIAvailable)
        {
            var heelsPlugin = _pluginInterface.InstalledPlugins
                .FirstOrDefault(p => string.Equals(p.InternalName, "SimpleHeels", StringComparison.OrdinalIgnoreCase));
            if (heelsPlugin != null)
            {
                compatiblePlugins.Add(new PluginInfo("Simple Heels", "SimpleHeels"));
                // Add Livepose as a sub-feature of Simple Heels
                compatiblePlugins.Add(new PluginInfo("Livepose", "Livepose"));
            }
        }
        
        if (_ipcManager.Moodles.APIAvailable)
        {
            var moodlesPlugin = _pluginInterface.InstalledPlugins
                .FirstOrDefault(p => string.Equals(p.InternalName, "Moodles", StringComparison.OrdinalIgnoreCase));
            if (moodlesPlugin != null)
            {
                compatiblePlugins.Add(new PluginInfo("Moodles", "Moodles"));
            }
        }
        
        if (_ipcManager.Honorific.APIAvailable)
        {
            var honorificPlugin = _pluginInterface.InstalledPlugins
                .FirstOrDefault(p => string.Equals(p.InternalName, "Honorific", StringComparison.OrdinalIgnoreCase));
            if (honorificPlugin != null)
            {
                compatiblePlugins.Add(new PluginInfo("Honorific", "Honorific"));
            }
        }
        
        if (_ipcManager.PetNames.APIAvailable)
        {
            var petNamesPlugin = _pluginInterface.InstalledPlugins
                .FirstOrDefault(p => string.Equals(p.InternalName, "PetNicknames", StringComparison.OrdinalIgnoreCase));
            if (petNamesPlugin != null)
            {
                compatiblePlugins.Add(new PluginInfo("Pet Nicknames", "PetNicknames"));
            }
        }
        
        if (_ipcManager.Brio.APIAvailable)
        {
            var brioPlugin = _pluginInterface.InstalledPlugins
                .FirstOrDefault(p => string.Equals(p.InternalName, "Brio", StringComparison.OrdinalIgnoreCase));
            if (brioPlugin != null)
            {
                compatiblePlugins.Add(new PluginInfo("Brio", "Brio"));
            }
        }
        
        return compatiblePlugins;
    }
    
    private void OpenPlugin(string internalName)
    {
        try
        {
            // Map specific plugin internal names to their correct commands
            var command = internalName.ToLower() switch
            {
                "customizeplus" => "/customize",
                "simpleheels" => "/heels",
                "livepose" => "/heels livepose",
                _ => $"/{internalName.ToLower()}"
            };
            
            _logger.LogDebug("Attempting to open plugin with command: {Command}", command);
            
            // Execute the command using ICommandManager
            _commandManager.ProcessCommand(command);
            _logger.LogDebug("Successfully executed command: {Command}", command);
            


        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open plugin {PluginName}", internalName);
            _mediator.Publish(new NotificationMessage(
                "Plugin Error",
                $"Failed to open {internalName}: {ex.Message}",
                NotificationType.Error,
                TimeSpan.FromSeconds(5)
            ));
        }
    }
    
    private class PluginInfo
    {
        public string Name { get; }
        public string InternalName { get; }
        
        public PluginInfo(string name, string internalName)
        {
            Name = name;
            InternalName = internalName;
        }
    }
    
    protected override void DrawInternal()
    {
        var iconSize = 32f;
        var padding = 4f;
        var windowSize = iconSize + padding * 2;
        
        // Set window size to fit the icon with padding
        ImGui.SetWindowSize(new Vector2(windowSize, windowSize), ImGuiCond.Always);
        
        // Get current window position
        var currentPos = ImGui.GetWindowPos();
        
        // Draw the Sphene logo/icon
        var drawList = ImGui.GetWindowDrawList();
        var iconPos = new Vector2(currentPos.X + padding, currentPos.Y + padding);
        var iconColor = ImGui.ColorConvertFloat4ToU32(SpheneColors.SpheneGold);
        
        // Draw Sphene Logo or fallback
        if (_spheneLogoTexture != null)
        {
            drawList.AddImage(_spheneLogoTexture.Handle, iconPos, iconPos + new Vector2(iconSize, iconSize));
        }
        else
        {
            // Fallback: Draw a simple circle as icon
            drawList.AddCircleFilled(new Vector2(iconPos.X + iconSize/2, iconPos.Y + iconSize/2), 
                iconSize/2 - 2, iconColor);
            
            // Add "S" text in the center
            using (_uiSharedService.UidFont.Push())
            {
                var text = "S";
                var textSize = ImGui.CalcTextSize(text);
                var textPos = new Vector2(
                    iconPos.X + (iconSize - textSize.X) / 2,
                    iconPos.Y + (iconSize - textSize.Y) / 2
                );
                drawList.AddText(textPos, ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 1)), text);
            }
        }
        
        // Draw status indicator
        DrawStatusIndicator(drawList, iconPos, iconSize);
        
        // Draw update indicator if available
        if (_updateAvailable)
        {
            DrawUpdateIndicator(drawList, iconPos, iconSize);
        }
        
        // Handle dragging and clicking for the entire window
        ImGui.SetCursorPos(new Vector2(0, 0));
        ImGui.InvisibleButton("##sphene_icon_window", new Vector2(windowSize, windowSize));
        
        // Handle mouse interactions
        if (ImGui.IsItemActive())
        {
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                _wasClicked = true;
                _clickStartPos = ImGui.GetMousePos();
            }
            
            // Check if mouse is being dragged
            if (ImGui.IsMouseDragging(ImGuiMouseButton.Left, 3.0f)) // 3 pixel threshold for drag
            {
                if (!_isDragging)
                {
                    _isDragging = true;
                    _wasClicked = false; // Cancel click if we start dragging
                    _iconPosition = ImGui.GetWindowPos(); // Update current position when drag starts
                }
                
                var mouseDelta = ImGui.GetMouseDragDelta(ImGuiMouseButton.Left);
                var newPos = new Vector2(_iconPosition.X + mouseDelta.X, _iconPosition.Y + mouseDelta.Y);
                ImGui.SetWindowPos(newPos);
                _iconPosition = newPos; // Update stored position during drag
                ImGui.ResetMouseDragDelta(ImGuiMouseButton.Left);
            }
        }
        
        // Handle right-click for context menu - removed manual handling since BeginPopupContextItem handles this automatically
        
        // Handle mouse release
        if (ImGui.IsItemDeactivated())
        {
            if (_isDragging)
            {
                // Save position when drag ends
                _isDragging = false;
                _iconPosition = ImGui.GetWindowPos();
                _hasStoredIconPosition = true;
                SaveIconPositionToConfig();
                _logger.LogDebug("Icon position saved: {Position}", _iconPosition);
            }
            else if (_wasClicked)
            {
                // Handle click to toggle main window (only if not dragging)
                var currentMousePos = ImGui.GetMousePos();
                var dragDistance = Vector2.Distance(_clickStartPos, currentMousePos);
                
                if (dragDistance < 3.0f) // Only trigger click if mouse didn't move much
                {
                    ToggleMainWindow();
                }
            }
            
            _wasClicked = false;
        }
        
        // Show tooltip
        if (ImGui.IsItemHovered())
        {
            using (SpheneCustomTheme.ApplyTooltipTheme())
            {
                ImGui.BeginTooltip();
                ImGui.Text("Click to toggle Sphene | Hold and drag to move | Right-click for menu");
                ImGui.Separator();
                ImGui.Text($"Server Status: {GetStatusText(_apiController.ServerState)}");
                if (_updateAvailable && _updateInfo != null)
                {
                    ImGui.Separator();
                    ImGui.Text($"Update available: {_updateInfo.CurrentVersion} -> {_updateInfo.LatestVersion}");
                }
                ImGui.EndTooltip();
            }
        }
        
        // Update stored position if window was moved
        if (!_isDragging)
        {
            var windowPos = ImGui.GetWindowPos();
            if (windowPos != _iconPosition)
            {
                _iconPosition = windowPos;
                if (_hasStoredIconPosition)
                {
                    SaveIconPositionToConfig();
                }
            }
        }
        
        // Draw context menu
        DrawContextMenu();
    }
    
    private void ToggleMainWindow()
    {
        _logger.LogDebug("Toggling main window");
        
        if (_configService.Current.HasValidSetup())
        {
            _mediator.Publish(new UiToggleMessage(typeof(CompactUi)));
        }
        else
        {
            _mediator.Publish(new UiToggleMessage(typeof(IntroUi)));
        }
    }
    
    private void LoadIconPositionFromConfig()
    {
        var savedX = _configService.Current.IconPositionX;
        var savedY = _configService.Current.IconPositionY;
        
        if (savedX >= 0 && savedY >= 0)
        {
            _iconPosition = new Vector2(savedX, savedY);
            _hasStoredIconPosition = true;
        }
    }
    
    private void SaveIconPositionToConfig()
    {
        _logger.LogDebug("Saving icon position: {X}, {Y}", _iconPosition.X, _iconPosition.Y);
        _configService.Current.IconPositionX = _iconPosition.X;
        _configService.Current.IconPositionY = _iconPosition.Y;
        _configService.Save();
    }
    
    private void LoadSpheneLogoTexture()
    {
        try
        {
            if (!string.IsNullOrEmpty(SpheneImages.SpheneLogoBase64))
            {
                var imageData = Convert.FromBase64String(SpheneImages.SpheneLogoBase64);
                _spheneLogoTexture = _uiSharedService.LoadImage(imageData);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Sphene logo texture");
        }
    }
    
    private void DrawStatusIndicator(ImDrawListPtr drawList, Vector2 iconPos, float iconSize)
    {
        var indicatorRadius = 4f;
        var indicatorPos = new Vector2(
            iconPos.X + iconSize - indicatorRadius - 2f,
            iconPos.Y + indicatorRadius + 2f
        );
        
        var statusColor = GetStatusColor(_apiController.ServerState);
        var indicatorColor = ImGui.ColorConvertFloat4ToU32(statusColor);
        
        // Draw status indicator circle
        drawList.AddCircleFilled(indicatorPos, indicatorRadius, indicatorColor);
        
        // Draw white border around indicator for better visibility
        drawList.AddCircle(indicatorPos, indicatorRadius, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.8f)), 0, 1f);
    }

    // Draw a green arrow overlay indicating an available update
    private void DrawUpdateIndicator(ImDrawListPtr drawList, Vector2 iconPos, float iconSize)
    {
        // Position near bottom-right corner and make it visually prominent
        var arrowText = FontAwesomeIcon.ArrowCircleUp.ToIconString();
        ImGui.PushFont(UiBuilder.IconFont);

        // Use a brighter green and add a subtle shadow for better readability
        var arrowColor = ImGui.ColorConvertFloat4ToU32(ImGuiColors.HealerGreen);
        var shadowColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 0.85f));

        // Anchor near bottom-right of the icon
        var overlayPos = new Vector2(iconPos.X + iconSize - 14f, iconPos.Y + iconSize - 14f);

        // Draw a semi-transparent dark bubble behind the arrow to improve contrast
        var bubbleCenter = new Vector2(overlayPos.X + 6f, overlayPos.Y + 8f);
        drawList.AddCircleFilled(bubbleCenter, 9f, ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 0.5f)));

        // Shadow then arrow
        drawList.AddText(new Vector2(overlayPos.X + 1f, overlayPos.Y + 1f), shadowColor, arrowText);
        drawList.AddText(overlayPos, arrowColor, arrowText);

        ImGui.PopFont();
    }

    private void OnUpdateAvailable(ShowUpdateNotificationMessage msg)
    {
        _updateInfo = msg.UpdateInfo;
        _updateAvailable = _updateInfo?.IsUpdateAvailable == true;
        _logger.LogDebug("Update notification received. Available: {available}, Latest: {latest}", _updateAvailable, _updateInfo?.LatestVersion);
        
        if (_updateAvailable && !_updateToastShown)
        {
            // Publish a toast notification via NotificationService
            Mediator.Publish(new NotificationMessage(
                "Sphene Update",
                $"Update available: {_updateInfo!.LatestVersion}",
                NotificationType.Info,
                TimeShownOnScreen: TimeSpan.FromSeconds(10)));
            _updateToastShown = true;
        }
    }
    
    private Vector4 GetStatusColor(ServerState serverState)
    {
        return serverState switch
        {
            ServerState.Connected => SpheneColors.NetworkConnected,
            ServerState.Connecting => SpheneColors.NetworkWarning,
            ServerState.Reconnecting => SpheneColors.NetworkWarning,
            ServerState.Disconnected => SpheneColors.NetworkDisconnected,
            ServerState.Disconnecting => SpheneColors.NetworkWarning,
            ServerState.Offline => SpheneColors.NetworkInactive,
            ServerState.Unauthorized => SpheneColors.NetworkError,
            ServerState.VersionMisMatch => SpheneColors.NetworkError,
            ServerState.RateLimited => SpheneColors.NetworkError,
            ServerState.NoSecretKey => SpheneColors.NetworkError,
            ServerState.MultiChara => SpheneColors.NetworkError,
            ServerState.OAuthMisconfigured => SpheneColors.NetworkError,
            ServerState.OAuthLoginTokenStale => SpheneColors.NetworkError,
            ServerState.NoAutoLogon => SpheneColors.NetworkInactive,
            _ => SpheneColors.NetworkInactive
        };
    }
    
    private string GetStatusText(ServerState serverState)
    {
        return serverState switch
        {
            ServerState.Connected => "Connected",
            ServerState.Connecting => "Connecting...",
            ServerState.Reconnecting => "Reconnecting...",
            ServerState.Disconnected => "Disconnected",
            ServerState.Disconnecting => "Disconnecting...",
            ServerState.Offline => "Server Offline",
            ServerState.Unauthorized => "Unauthorized",
            ServerState.VersionMisMatch => "Version Mismatch",
            ServerState.RateLimited => "Rate Limited",
            ServerState.NoSecretKey => "No Secret Key",
            ServerState.MultiChara => "Duplicate Characters",
            ServerState.OAuthMisconfigured => "OAuth Misconfigured",
            ServerState.OAuthLoginTokenStale => "OAuth Token Stale",
            ServerState.NoAutoLogon => "Auto Login Disabled",
            _ => "Unknown"
        };
    }
    
    private void OnConfigurationChanged(object? sender, EventArgs e)
    {
        // Update icon visibility when configuration changes
        IsOpen = _configService.Current.ShowSpheneIcon;
    }
    
    public void Dispose()
    {
        _configService.ConfigSave -= OnConfigurationChanged;
        _spheneLogoTexture?.Dispose();
        _logger.LogDebug("SpheneIcon disposed");
    }
}