using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
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
using Sphene.UI.Panels;
using Sphene.UI.Theme;

namespace Sphene.UI.Components;

public class SpheneIcon : WindowMediatorSubscriberBase
{
    private new readonly ILogger<SpheneIcon> _logger;
    private readonly SpheneMediator _mediator;
    private readonly SpheneConfigService _configService;
    private readonly UiSharedService _uiSharedService;
    private readonly ApiController _apiController;
    private readonly IpcManager _ipcManager;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ICommandManager _commandManager;
    private readonly ShrinkUHostService _shrinkuHostService;
    // Built-in ShrinkU integration
    private readonly ShrinkU.Configuration.ShrinkUConfigService _shrinkuConfig;
    private readonly ShrinkU.UI.ConversionUI _shrinkuConversion;
    private readonly ShrinkU.UI.FirstRunSetupUI _shrinkuFirstRun;
    
    private Vector2 _iconPosition = new Vector2(100, 100);
    private bool _hasStoredIconPosition = false;
    private bool _isDragging = false;
    private bool _wasClicked = false;
    private Vector2 _clickStartPos;
    private Vector2 _dragStartMousePos;
    private Vector2 _dragStartIconPos;
    private DateTime _mouseDownAt;
    private Vector2 _lastSavedIconPosition = new(float.NaN, float.NaN);
    private const int DragStartDelayMs = 100; // delay (ms) before drag begins on hold
    private IDalamudTextureWrap? _spheneLogoTexture;
    
    // Context menu state
    // Removed _showContextMenu and _contextMenuPosition as they're no longer needed with BeginPopupContextItem
    
    // Update indicator state
    private bool _updateAvailable = false;
    private UpdateInfo? _updateInfo = null;
    private bool _updateToastShown = false;
    private bool _tooltipCacheInitialized = false;
    private bool _tooltipCachedIconLocked = false;
    private ServerState _tooltipCachedServerState = ServerState.Disconnected;
    private Version? _tooltipCachedUpdateFromVersion = null;
    private Version? _tooltipCachedUpdateToVersion = null;
    private string _tooltipHeaderText = string.Empty;
    private string _tooltipStatusText = string.Empty;
    private string _tooltipUpdateText = string.Empty;

    // Event badge tracking
    private readonly List<IconEvent> _activeEvents = new();
    private readonly Lock _eventsLock = new();
    private DateTimeOffset _lastEventAcknowledgeTime = DateTimeOffset.MinValue;
    private bool _pulseActive = false;

    // Tooltip cache for events
    private List<string> _cachedEventDescriptions = new();
    private int _lastEventCacheHash = 0;

    public SpheneIcon(ILogger<SpheneIcon> logger, SpheneMediator mediator, 
        SpheneConfigService configService, UiSharedService uiSharedService, ApiController apiController, 
        PerformanceCollectorService performanceCollectorService, IpcManager ipcManager, IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager, ShrinkUHostService shrinkuHostService,
        ShrinkU.Configuration.ShrinkUConfigService shrinkuConfig,
        ShrinkU.UI.ConversionUI shrinkuConversion,
        ShrinkU.UI.SettingsUI shrinkuSettings,
        ShrinkU.UI.FirstRunSetupUI shrinkuFirstRun) 
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
        _shrinkuHostService = shrinkuHostService;
        _shrinkuConfig = shrinkuConfig;
        _shrinkuConversion = shrinkuConversion;
        _ = shrinkuSettings;
        _shrinkuFirstRun = shrinkuFirstRun;
        
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

        // Subscribe to event badge messages
        Mediator.Subscribe<PenumbraModTransferAvailableMessage>(this, OnModTransferAvailable);
        Mediator.Subscribe<PenumbraModTransferCompletedMessage>(this, OnModTransferCompleted);
        Mediator.Subscribe<NotificationMessage>(this, OnNotification);
        Mediator.Subscribe<TestIconEventMessage>(this, OnTestIconEvent);

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
                var isShrinkU = string.Equals(plugin.InternalName, "ShrinkU.Builtin", StringComparison.OrdinalIgnoreCase);
                var enabled = !isShrinkU || _configService.Current.EnableShrinkUIntegration;
                if (ImGui.MenuItem($"Open {plugin.Name}", string.Empty, false, enabled))
                {
                    OpenPlugin(plugin.InternalName);
                }
            }
            
            if (availablePlugins.Count == 0)
            {
                ImGui.TextDisabled("No compatible plugins found");
            }

            ImGui.Separator();

            // Toggle icon lock state
            var locked = _configService.Current.LockSpheneIcon;
            if (ImGui.MenuItem("Lock Icon Position", string.Empty, locked, true))
            {
                _configService.Current.LockSpheneIcon = !locked;
                _configService.Save();
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
        // Always expose built-in ShrinkU
        compatiblePlugins.Add(new PluginInfo("ShrinkU", "ShrinkU.Builtin"));
        
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

        var bypassEmotePlugin = _pluginInterface.InstalledPlugins
            .FirstOrDefault(p => string.Equals(p.InternalName, "BypassEmote", StringComparison.OrdinalIgnoreCase));
        if (bypassEmotePlugin != null)
        {
            compatiblePlugins.Add(new PluginInfo("BypassEmote", "BypassEmote"));
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
                "bypassemote" => "/be",
                _ => $"/{internalName.ToLower()}"
            };
            
            // Built-in ShrinkU opens directly without chat commands
            if (string.Equals(internalName, "ShrinkU.Builtin", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Opening built-in ShrinkU UI");
                _shrinkuHostService.ApplyIntegrationEnabled(true);
                if (!_shrinkuConfig.Current.FirstRunCompleted)
                {
                    _shrinkuFirstRun.IsOpen = true;
                }
                else
                {
                    _shrinkuConversion.IsOpen = true;
                }
                return;
            }

            _logger.LogDebug("Attempting to open plugin with command: {Command}", command);
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
    
    private sealed class PluginInfo
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
        var cfg = _configService.Current;
        var iconSize = 32f;
        var padding = 4f;
        var pulsePadding = 14f; // Extra space so pulse rings are not clipped
        var windowSize = iconSize + padding * 2 + pulsePadding * 2;
        var iconLocked = cfg.LockSpheneIcon;

        // Get latest active event config to determine bounce and glow behavior
        var latestEventType = GetLatestUnacknowledgedEventType();
        var latestEventConfig = GetEventConfig(latestEventType, cfg);

        // Determine bounce: use per-event settings if active, otherwise permanent settings
        var bounceActive = cfg.IconPermEffectBounce || (latestEventType != IconEventType.None && latestEventConfig.EffectBounce);
        var bounceIntensity = latestEventType != IconEventType.None && latestEventConfig.EffectBounce
            ? latestEventConfig.BounceIntensity
            : cfg.IconPermBounceIntensity;
        var bounceSpeed = latestEventType != IconEventType.None && latestEventConfig.EffectBounce
            ? latestEventConfig.BounceSpeed
            : cfg.IconPermBounceSpeed;
        var bounceScale = bounceActive
            ? 1.0f + bounceIntensity * MathF.Sin(_pluginInterface.UiBuilder.FrameCount * bounceSpeed * 0.05f)
            : 1.0f;
        var scaledIconSize = iconSize * bounceScale;
        var scaledOffset = (iconSize - scaledIconSize) / 2f;

        // Set window size large enough to contain pulse rings and glow outside the icon
        ImGui.SetWindowSize(new Vector2(windowSize, windowSize), ImGuiCond.Always);

        // Get current window position
        var currentPos = ImGui.GetWindowPos();

        // Prune expired events before drawing
        PruneExpiredEvents();

        // Draw the Sphene logo/icon
        var drawList = ImGui.GetWindowDrawList();
        var baseIconPos = new Vector2(currentPos.X + padding + pulsePadding, currentPos.Y + padding + pulsePadding);
        var iconPos = new Vector2(baseIconPos.X + scaledOffset, baseIconPos.Y + scaledOffset);
        var iconAlpha = cfg.IconGlobalAlpha;
        var iconColorVec = SpheneColors.SpheneGold;
        iconColorVec.W *= iconAlpha;
        var iconColor = ImGui.ColorConvertFloat4ToU32(iconColorVec);

        // Determine glow: use most recent event glow (or permanent if no events)
        var glowActive = cfg.IconPermEffectGlow || (latestEventType != IconEventType.None && latestEventConfig.EffectGlow);
        var glowColor = latestEventType != IconEventType.None && latestEventConfig.EffectGlow
            ? latestEventConfig.Color
            : cfg.IconPermColor;
        var glowIntensity = latestEventType != IconEventType.None && latestEventConfig.EffectGlow
            ? latestEventConfig.GlowIntensity
            : cfg.IconPermGlowIntensity;
        var glowRadius = latestEventType != IconEventType.None && latestEventConfig.EffectGlow
            ? latestEventConfig.GlowRadius
            : cfg.IconPermGlowRadius;
        if (glowActive)
        {
            DrawGlow(drawList, iconPos, scaledIconSize, glowColor, glowIntensity, glowRadius, iconAlpha);
        }

        // Draw permanent weak purple pulse
        if (cfg.IconPermEffectPulse)
        {
            DrawPermanentPurplePulse(drawList, baseIconPos, iconSize,
                cfg.IconPermAlpha, cfg.IconPermPulseMinRadius, cfg.IconPermPulseMaxRadius,
                cfg.IconPermColor, cfg.IconPermEffectRainbow, cfg.IconRainbowSpeed, iconAlpha);
        }

        // Draw pulse ring behind icon if events are active (stronger pulse)
        if (_pulseActive && latestEventType != IconEventType.None && latestEventConfig.EffectPulse)
        {
            DrawPulseRing(drawList, baseIconPos, iconSize,
                (latestEventConfig.Color, latestEventConfig.Alpha, latestEventConfig.EffectRainbow,
                 latestEventConfig.PulseMinRadius, latestEventConfig.PulseMaxRadius), iconAlpha, cfg.IconRainbowSpeed);
        }

        // Draw Sphene Logo or fallback
        if (_spheneLogoTexture != null)
        {
            var tintColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, iconAlpha));
            drawList.AddImage(_spheneLogoTexture.Handle, iconPos, iconPos + new Vector2(scaledIconSize, scaledIconSize),
                Vector2.Zero, Vector2.One, tintColor);
        }
        else
        {
            // Fallback: Draw a simple circle as icon
            drawList.AddCircleFilled(new Vector2(iconPos.X + scaledIconSize/2, iconPos.Y + scaledIconSize/2),
                scaledIconSize/2 - 2, iconColor);

            // Add "S" text in the center
            using (_uiSharedService.UidFont.Push())
            {
                var text = "S";
                var textSize = ImGui.CalcTextSize(text);
                var textPos = new Vector2(
                    iconPos.X + (scaledIconSize - textSize.X) / 2,
                    iconPos.Y + (scaledIconSize - textSize.Y) / 2
                );
                var textColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, iconAlpha));
                drawList.AddText(textPos, textColor, text);
            }
        }

        // Draw status indicator
        DrawStatusIndicator(drawList, iconPos, scaledIconSize, iconAlpha);

        // Draw update indicator if available
        if (_updateAvailable)
        {
            DrawUpdateIndicator(drawList, iconPos, scaledIconSize, iconAlpha);
        }

        // Draw event badges
        DrawEventBadges(drawList, iconPos, scaledIconSize, cfg);

        // Handle dragging and clicking only for the icon area (rest of window is click-through)
        ImGui.SetCursorPos(new Vector2(padding + pulsePadding, padding + pulsePadding));
        ImGui.InvisibleButton("##sphene_icon_window", new Vector2(iconSize, iconSize));
        
        // Handle mouse interactions (press-and-hold before dragging)
        // Start tracking on left-press over the icon
        if (ImGui.IsItemActivated() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            _wasClicked = true;
            _isDragging = false;
            _clickStartPos = ImGui.GetMousePos();
            _dragStartMousePos = _clickStartPos; // initial reference; will be updated on drag start
            _dragStartIconPos = ImGui.GetWindowPos();
            _mouseDownAt = DateTime.UtcNow;
        }

        // While holding, only begin dragging after a short hold delay (if not locked)
        if (!iconLocked && ImGui.IsItemActive() && ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            var elapsedMs = (DateTime.UtcNow - _mouseDownAt).TotalMilliseconds;
            var currentMouse = ImGui.GetMousePos();

            if (!_isDragging && elapsedMs >= DragStartDelayMs)
            {
                _isDragging = true;
                _wasClicked = false;
                _dragStartMousePos = currentMouse;
                _dragStartIconPos = ImGui.GetWindowPos();
            }

            if (_isDragging)
            {
                var delta = new Vector2(currentMouse.X - _dragStartMousePos.X, currentMouse.Y - _dragStartMousePos.Y);
                var newPos = new Vector2(_dragStartIconPos.X + delta.X, _dragStartIconPos.Y + delta.Y);
                ImGui.SetWindowPos(newPos);
                _iconPosition = newPos;
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
                    AcknowledgeEvents();
                }
            }
            
            _wasClicked = false;
        }
        
        // Show tooltip
        if (ImGui.IsItemHovered())
        {
            RefreshTooltipCache(iconLocked);
            using (SpheneCustomTheme.ApplyTooltipTheme())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(_tooltipHeaderText);
                ImGui.Separator();
                ImGui.TextUnformatted(_tooltipStatusText);
                if (_updateAvailable && _updateInfo != null && !string.IsNullOrEmpty(_tooltipUpdateText))
                {
                    ImGui.Separator();
                    ImGui.TextUnformatted(_tooltipUpdateText);
                }
                if (_cachedEventDescriptions.Count > 0)
                {
                    ImGui.Separator();
                    foreach (var description in _cachedEventDescriptions)
                    {
                        ImGui.TextUnformatted(description);
                    }
                }
                ImGui.EndTooltip();
            }
        }
        
        // Draw context menu
        DrawContextMenu();
    }

    private void RefreshTooltipCache(bool iconLocked)
    {
        var currentState = _apiController.ServerState;
        var currentFromVersion = _updateInfo?.CurrentVersion;
        var currentToVersion = _updateInfo?.LatestVersion;
        var currentEventsHash = GetEventsCacheHash();

        if (_tooltipCacheInitialized
            && _tooltipCachedIconLocked == iconLocked
            && _tooltipCachedServerState == currentState
            && EqualityComparer<Version?>.Default.Equals(_tooltipCachedUpdateFromVersion, currentFromVersion)
            && EqualityComparer<Version?>.Default.Equals(_tooltipCachedUpdateToVersion, currentToVersion)
            && _lastEventCacheHash == currentEventsHash)
        {
            return;
        }

        _tooltipCacheInitialized = true;
        _tooltipCachedIconLocked = iconLocked;
        _tooltipCachedServerState = currentState;
        _tooltipCachedUpdateFromVersion = currentFromVersion;
        _tooltipCachedUpdateToVersion = currentToVersion;
        _lastEventCacheHash = currentEventsHash;

        _tooltipHeaderText = iconLocked
            ? "Click to toggle Sphene | Drag is locked | Right-click for menu"
            : "Click to toggle Sphene | Hold and drag to move | Right-click for menu";
        _tooltipStatusText = "Server Status: " + GetStatusText(currentState);
        _tooltipUpdateText = (_updateAvailable && _updateInfo != null)
            ? $"Update available: {_updateInfo.CurrentVersion} -> {_updateInfo.LatestVersion}"
            : string.Empty;

        // Build event descriptions for tooltip
        lock (_eventsLock)
        {
            _cachedEventDescriptions = _activeEvents
                .Where(e => e.Timestamp > _lastEventAcknowledgeTime)
                .Select(e => e.Description)
                .ToList();
        }
    }

    private int GetEventsCacheHash()
    {
        lock (_eventsLock)
        {
            var hash = _activeEvents.Count.GetHashCode();
            foreach (var evt in _activeEvents)
            {
                hash = HashCode.Combine(hash, evt.Type, evt.Description);
            }
            return hash;
        }
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
        if (Math.Abs(_iconPosition.X - _lastSavedIconPosition.X) < 0.5f
            && Math.Abs(_iconPosition.Y - _lastSavedIconPosition.Y) < 0.5f)
        {
            return;
        }

        _logger.LogDebug("Saving icon position: {X}, {Y}", _iconPosition.X, _iconPosition.Y);
        _configService.Current.IconPositionX = _iconPosition.X;
        _configService.Current.IconPositionY = _iconPosition.Y;
        _configService.Save();
        _lastSavedIconPosition = _iconPosition;
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
    
    private void DrawStatusIndicator(ImDrawListPtr drawList, Vector2 iconPos, float iconSize, float alpha = 1.0f)
    {
        var indicatorRadius = 4f;
        var indicatorPos = new Vector2(
            iconPos.X + iconSize - indicatorRadius - 2f,
            iconPos.Y + indicatorRadius + 2f
        );

        var statusColor = _apiController.IsTransientDisconnectInProgress
            ? Sphene.UI.Theme.SpheneCustomTheme.CurrentTheme.CompactServerStatusWarning
            : GetStatusColor(_apiController.ServerState);
        statusColor.W *= alpha;
        var indicatorColor = ImGui.ColorConvertFloat4ToU32(statusColor);

        // Draw status indicator circle
        drawList.AddCircleFilled(indicatorPos, indicatorRadius, indicatorColor);

        // Draw white border around indicator for better visibility
        var borderColor = new Vector4(1, 1, 1, 0.8f * alpha);
        drawList.AddCircle(indicatorPos, indicatorRadius, ImGui.ColorConvertFloat4ToU32(borderColor), 0, 1f);
    }

    // Draw a green arrow overlay indicating an available update
    private static void DrawUpdateIndicator(ImDrawListPtr drawList, Vector2 iconPos, float iconSize, float alpha = 1.0f)
    {
        var arrowText = FontAwesomeIcon.ArrowCircleUp.ToIconString();
        var textSize = ImGui.CalcTextSize(arrowText);
        var arrowPos = new Vector2(
            iconPos.X + iconSize - textSize.X - 4f,
            iconPos.Y + iconSize - textSize.Y - 4f
        );

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var green = new Vector4(0.2f, 0.9f, 0.2f, alpha);
            drawList.AddText(arrowPos, ImGui.ColorConvertFloat4ToU32(green), arrowText);
        }
    }

    private void OnUpdateAvailable(ShowUpdateNotificationMessage msg)
    {
        _updateInfo = msg.UpdateInfo;
        _updateAvailable = _updateInfo?.IsUpdateAvailable == true;
        _logger.LogDebug("Update notification received. Available: {available}, Latest: {latest}", _updateAvailable, _updateInfo?.LatestVersion);
        
        if (_updateAvailable && !_updateToastShown)
        {
            var toastText = _updateInfo!.IsTestBuildUpdate
                ? $"Testbuild update available: {_updateInfo.LatestVersion}"
                : $"Update available: {_updateInfo.LatestVersion}";
            // Publish a toast notification via NotificationService
            Mediator.Publish(new NotificationMessage(
                "Sphene Update",
                toastText,
                NotificationType.Info,
                TimeShownOnScreen: TimeSpan.FromSeconds(10)));
            _updateToastShown = true;
        }
    }
    
    private static Vector4 GetStatusColor(ServerState serverState)
    {
        var t = Sphene.UI.Theme.SpheneCustomTheme.CurrentTheme;
        return serverState switch
        {
            ServerState.Connected => t.CompactServerStatusConnected,
            ServerState.Connecting => t.CompactServerStatusWarning,
            ServerState.Reconnecting => t.CompactServerStatusWarning,
            ServerState.Disconnected => t.CompactServerStatusWarning,
            ServerState.Disconnecting => t.CompactServerStatusWarning,
            ServerState.Offline => t.CompactServerStatusError,
            ServerState.Unauthorized => t.CompactServerStatusError,
            ServerState.VersionMisMatch => t.CompactServerStatusError,
            ServerState.RateLimited => t.CompactServerStatusWarning,
            ServerState.NoSecretKey => t.CompactServerStatusWarning,
            ServerState.MultiChara => t.CompactServerStatusWarning,
            ServerState.OAuthMisconfigured => t.CompactServerStatusError,
            ServerState.OAuthLoginTokenStale => t.CompactServerStatusError,
            ServerState.NoAutoLogon => t.CompactServerStatusWarning,
            _ => t.CompactServerStatusError
        };
    }
    
    private static string GetStatusText(ServerState serverState)
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
    
    private int GetEffectDuration(IconEventType type)
    {
        var cfg = _configService.Current;
        return type switch
        {
            IconEventType.ModTransferAvailable or IconEventType.ModTransferCompleted => cfg.IconModTransferEffectDurationSeconds,
            IconEventType.PairRequest => cfg.IconPairRequestEffectDurationSeconds,
            IconEventType.Notification => cfg.IconNotificationEffectDurationSeconds,
            _ => cfg.IconEventExpirySeconds
        };
    }

    private int GetBadgeDuration(IconEventType type)
    {
        var cfg = _configService.Current;
        return type switch
        {
            IconEventType.ModTransferAvailable or IconEventType.ModTransferCompleted => cfg.IconModTransferBadgeDurationSeconds,
            IconEventType.PairRequest => cfg.IconPairRequestBadgeDurationSeconds,
            IconEventType.Notification => cfg.IconNotificationBadgeDurationSeconds,
            _ => cfg.IconEventExpirySeconds
        };
    }

    private bool IsEffectActive(IconEvent e)
    {
        var duration = GetEffectDuration(e.Type);
        if (duration <= 0) return e.Timestamp > _lastEventAcknowledgeTime;
        return e.Timestamp > _lastEventAcknowledgeTime && (DateTimeOffset.UtcNow - e.Timestamp <= TimeSpan.FromSeconds(duration));
    }

    private bool IsBadgeActive(IconEvent e)
    {
        var duration = GetBadgeDuration(e.Type);
        if (duration <= 0) return e.Timestamp > _lastEventAcknowledgeTime;
        return e.Timestamp > _lastEventAcknowledgeTime && (DateTimeOffset.UtcNow - e.Timestamp <= TimeSpan.FromSeconds(duration));
    }

    private void PruneExpiredEvents()
    {
        var now = DateTimeOffset.UtcNow;
        var hadEvents = false;

        lock (_eventsLock)
        {
            hadEvents = _activeEvents.Count > 0;
            _activeEvents.RemoveAll(e =>
            {
                var effectDuration = GetEffectDuration(e.Type);
                var badgeDuration = GetBadgeDuration(e.Type);
                var effectExpired = effectDuration > 0 && (now - e.Timestamp > TimeSpan.FromSeconds(effectDuration));
                var badgeAcknowledged = e.Timestamp <= _lastEventAcknowledgeTime;
                var badgeExpired = !badgeAcknowledged && badgeDuration > 0 && (now - e.Timestamp > TimeSpan.FromSeconds(badgeDuration));
                // Remove if effect expired AND (badge expired or badge acknowledged)
                return effectExpired && (badgeExpired || badgeAcknowledged);
            });
            _pulseActive = _activeEvents.Any(IsEffectActive);
        }

        if (hadEvents && !_pulseActive)
        {
            _cachedEventDescriptions.Clear();
        }
    }

    private void DrawGlow(ImDrawListPtr drawList, Vector2 iconPos, float iconSize,
        uint glowColor, float glowIntensity, float glowRadius, float globalAlpha)
    {
        var center = new Vector2(iconPos.X + iconSize / 2, iconPos.Y + iconSize / 2);
        var glowColorVec = ImGui.ColorConvertU32ToFloat4(glowColor);
        var baseRadius = iconSize / 2f;

        for (int i = 0; i < 5; i++)
        {
            var layerAlpha = glowIntensity * (1.0f - i * 0.2f) * globalAlpha;
            var radius = baseRadius * (1.0f + i * 0.12f * glowRadius);
            var color = new Vector4(glowColorVec.X, glowColorVec.Y, glowColorVec.Z, layerAlpha);
            drawList.AddCircleFilled(center, radius, ImGui.ColorConvertFloat4ToU32(color));
        }
    }

    private void DrawPulseRing(ImDrawListPtr drawList, Vector2 iconPos, float iconSize,
        (uint Color, float Alpha, bool EffectRainbow, float MinRadius, float MaxRadius) eventConfig,
        float globalAlpha, float rainbowSpeed)
    {
        var center = new Vector2(iconPos.X + iconSize / 2, iconPos.Y + iconSize / 2);

        // Outward ripple: ring expands from icon center and fades to nothing (~1.5s cycle)
        var cycleDuration = 1.5;
        var t = (float)((DateTimeOffset.UtcNow.Ticks % (long)(cycleDuration * 10_000_000L)) / (cycleDuration * 10_000_000L));

        var minRadius = iconSize * eventConfig.MinRadius;
        var maxRadius = iconSize * eventConfig.MaxRadius;
        var radius = minRadius + (maxRadius - minRadius) * t;

        // Alpha fades out quadratically as ring expands
        var alpha = eventConfig.Alpha * (1f - t * t) * globalAlpha;

        // Determine color from per-event config (rainbow overrides)
        var color = eventConfig.EffectRainbow
            ? GetRainbowColor(rainbowSpeed, alpha)
            : ImGui.ColorConvertU32ToFloat4(eventConfig.Color);
        var uintColor = ImGui.ColorConvertFloat4ToU32(new Vector4(color.X, color.Y, color.Z, alpha));

        drawList.AddCircle(center, radius, uintColor, 32, 2.5f);
    }

    private void DrawPermanentPurplePulse(ImDrawListPtr drawList, Vector2 iconPos, float iconSize,
        float alpha, float minRadius, float maxRadius, uint color, bool rainbow, float rainbowSpeed, float globalAlpha)
    {
        var center = new Vector2(iconPos.X + iconSize / 2, iconPos.Y + iconSize / 2);

        // Outward ripple: ring expands from icon center and fades to nothing (~2.5s cycle)
        var cycleDuration = 2.5;
        var t = (float)((DateTimeOffset.UtcNow.Ticks % (long)(cycleDuration * 10_000_000L)) / (cycleDuration * 10_000_000L));

        var radiusMin = iconSize * minRadius;
        var radiusMax = iconSize * maxRadius;
        var radius = radiusMin + (radiusMax - radiusMin) * t;

        // Alpha fades out quadratically as ring expands — full at start, gone at outer edge
        var pulseAlpha = alpha * (1f - t * t) * globalAlpha;

        var rgb = rainbow
            ? GetRainbowColor(rainbowSpeed * 0.7f, pulseAlpha)
            : ImGui.ColorConvertU32ToFloat4(color);
        var pulseColor = new Vector4(rgb.X, rgb.Y, rgb.Z, pulseAlpha);
        var uintColor = ImGui.ColorConvertFloat4ToU32(pulseColor);

        drawList.AddCircle(center, radius, uintColor, 32, 2.5f);
    }

    private Vector4 GetRainbowColor(float speed, float alpha)
    {
        var hue = ((_pluginInterface.UiBuilder.FrameCount * speed * 0.5f) % 360f) / 360f;
        var rgb = HsvToRgb(hue, 1.0f, 1.0f);
        return new Vector4(rgb.X, rgb.Y, rgb.Z, alpha);
    }

    private static Vector3 HsvToRgb(float h, float s, float v)
    {
        var i = (int)(h * 6f);
        var f = h * 6f - i;
        var p = v * (1f - s);
        var q = v * (1f - f * s);
        var t = v * (1f - (1f - f) * s);

        i = Math.Abs(i % 6);
        return i switch
        {
            0 => new Vector3(v, t, p),
            1 => new Vector3(q, v, p),
            2 => new Vector3(p, v, t),
            3 => new Vector3(p, q, v),
            4 => new Vector3(t, p, v),
            _ => new Vector3(v, p, q),
        };
    }

    private Vector4 GetPulseColor()
    {
        var cfg = _configService.Current;
        lock (_eventsLock)
        {
            var latest = _activeEvents
                .Where(IsEffectActive)
                .OrderByDescending(e => e.Timestamp)
                .FirstOrDefault();

            if (latest == null) return SpheneColors.SpheneGold;

            return latest.Type switch
            {
                IconEventType.ModTransferAvailable => ImGui.ColorConvertU32ToFloat4(cfg.IconModTransferColor),
                IconEventType.ModTransferCompleted => ImGui.ColorConvertU32ToFloat4(cfg.IconModTransferColor),
                IconEventType.PairRequest => ImGui.ColorConvertU32ToFloat4(cfg.IconPairRequestColor),
                IconEventType.Notification => ImGui.ColorConvertU32ToFloat4(cfg.IconNotificationColor),
                _ => SpheneColors.SpheneGold
            };
        }
    }

    private IconEventType GetLatestUnacknowledgedEventType()
    {
        lock (_eventsLock)
        {
            var latest = _activeEvents
                .Where(IsEffectActive)
                .OrderByDescending(e => e.Timestamp)
                .FirstOrDefault();
            return latest?.Type ?? IconEventType.None;
        }
    }

    private (uint Color, float Alpha, bool EffectPulse, bool EffectGlow, bool EffectBounce, bool EffectRainbow,
        float PulseMinRadius, float PulseMaxRadius, float GlowIntensity, float GlowRadius,
        float BounceIntensity, float BounceSpeed)
        GetEventConfig(IconEventType type, Sphene.SpheneConfiguration.Configurations.SpheneConfig cfg)
    {
        return type switch
        {
            IconEventType.ModTransferAvailable or IconEventType.ModTransferCompleted =>
                (cfg.IconModTransferColor, cfg.IconModTransferAlpha,
                 cfg.IconModTransferEffectPulse, cfg.IconModTransferEffectGlow,
                 cfg.IconModTransferEffectBounce, cfg.IconModTransferEffectRainbow,
                 cfg.IconModTransferPulseMinRadius, cfg.IconModTransferPulseMaxRadius,
                 cfg.IconModTransferGlowIntensity, cfg.IconModTransferGlowRadius,
                 cfg.IconModTransferBounceIntensity, cfg.IconModTransferBounceSpeed),
            IconEventType.PairRequest =>
                (cfg.IconPairRequestColor, cfg.IconPairRequestAlpha,
                 cfg.IconPairRequestEffectPulse, cfg.IconPairRequestEffectGlow,
                 cfg.IconPairRequestEffectBounce, cfg.IconPairRequestEffectRainbow,
                 cfg.IconPairRequestPulseMinRadius, cfg.IconPairRequestPulseMaxRadius,
                 cfg.IconPairRequestGlowIntensity, cfg.IconPairRequestGlowRadius,
                 cfg.IconPairRequestBounceIntensity, cfg.IconPairRequestBounceSpeed),
            IconEventType.Notification =>
                (cfg.IconNotificationColor, cfg.IconNotificationAlpha,
                 cfg.IconNotificationEffectPulse, cfg.IconNotificationEffectGlow,
                 cfg.IconNotificationEffectBounce, cfg.IconNotificationEffectRainbow,
                 cfg.IconNotificationPulseMinRadius, cfg.IconNotificationPulseMaxRadius,
                 cfg.IconNotificationGlowIntensity, cfg.IconNotificationGlowRadius,
                 cfg.IconNotificationBounceIntensity, cfg.IconNotificationBounceSpeed),
            _ => (0xFFE86699u, 0.3f, false, false, false, false, 0.46f, 0.6f, 0.6f, 1.2f, 0.12f, 1.5f)
        };
    }

    private void DrawEventBadges(ImDrawListPtr drawList, Vector2 iconPos, float iconSize, Sphene.SpheneConfiguration.Configurations.SpheneConfig cfg)
    {
        lock (_eventsLock)
        {
            var unacknowledged = _activeEvents
                .Where(IsBadgeActive)
                .Where(e => e.Type switch
                {
                    IconEventType.ModTransferAvailable or IconEventType.ModTransferCompleted => cfg.IconShowModTransferBadge,
                    IconEventType.PairRequest => cfg.IconShowPairRequestBadge,
                    IconEventType.Notification => cfg.IconShowNotificationBadge,
                    _ => true
                })
                .ToList();

            if (unacknowledged.Count == 0) return;

            var badgeRadius = 5f;
            var startX = iconPos.X + iconSize - badgeRadius;
            var startY = iconPos.Y + badgeRadius;
            var spacing = badgeRadius * 2.5f;

            for (var i = 0; i < unacknowledged.Count && i < 4; i++)
            {
                var evt = unacknowledged[i];
                var badgeColor = evt.Type switch
                {
                    IconEventType.ModTransferAvailable or IconEventType.ModTransferCompleted => ImGui.ColorConvertU32ToFloat4(cfg.IconModTransferColor),
                    IconEventType.PairRequest => ImGui.ColorConvertU32ToFloat4(cfg.IconPairRequestColor),
                    IconEventType.Notification => ImGui.ColorConvertU32ToFloat4(cfg.IconNotificationColor),
                    _ => SpheneColors.SpheneGold
                };
                badgeColor.W *= cfg.IconGlobalAlpha;

                var pos = new Vector2(startX - i * spacing, startY);
                drawList.AddCircleFilled(pos, badgeRadius, ImGui.ColorConvertFloat4ToU32(badgeColor));
                var borderColor = new Vector4(1, 1, 1, 0.6f * cfg.IconGlobalAlpha);
                drawList.AddCircle(pos, badgeRadius, ImGui.ColorConvertFloat4ToU32(borderColor), 0, 1f);
            }

            // Overflow indicator if more than 4 events
            if (unacknowledged.Count > 4)
            {
                var pos = new Vector2(startX - 4 * spacing, startY);
                var overflowColor = new Vector4(0.5f, 0.5f, 0.5f, 0.8f * cfg.IconGlobalAlpha);
                drawList.AddCircleFilled(pos, badgeRadius, ImGui.ColorConvertFloat4ToU32(overflowColor));
            }
        }
    }

    private void AcknowledgeEvents()
    {
        _lastEventAcknowledgeTime = DateTimeOffset.UtcNow;
        _pulseActive = false;
        _cachedEventDescriptions.Clear();
    }

    private void AddEvent(IconEventType type, string description)
    {
        lock (_eventsLock)
        {
            _activeEvents.Add(new IconEvent(type, description, DateTimeOffset.UtcNow));
            // Keep max 20 events to prevent unbounded growth
            if (_activeEvents.Count > 20)
            {
                _activeEvents.RemoveAt(0);
            }
            _pulseActive = true;
        }
        _logger.LogDebug("Icon event added: {type} - {desc}", type, description);
    }

    private void OnModTransferAvailable(PenumbraModTransferAvailableMessage msg)
    {
        AddEvent(IconEventType.ModTransferAvailable, $"Mod transfer available: {msg.Notification.ModFolderName ?? "Unknown"}");
    }

    private void OnModTransferCompleted(PenumbraModTransferCompletedMessage msg)
    {
        AddEvent(IconEventType.ModTransferCompleted, $"Mod transfer completed: {msg.Notification.ModFolderName ?? "Unknown"}");
    }

    private void OnNotification(NotificationMessage msg)
    {
        if (msg.Type == NotificationType.Error || msg.Type == NotificationType.Warning)
        {
            AddEvent(IconEventType.Notification, $"{msg.Type}: {msg.Title}");
        }
    }

    private sealed record IconEvent(IconEventType Type, string Description, DateTimeOffset Timestamp);

    private enum IconEventType
    {
        None,
        ModTransferAvailable,
        ModTransferCompleted,
        PairRequest,
        Notification
    }

    private void OnTestIconEvent(TestIconEventMessage msg)
    {
        var type = msg.EventType switch
        {
            TestIconEventType.ModTransferAvailable => IconEventType.ModTransferAvailable,
            TestIconEventType.ModTransferCompleted => IconEventType.ModTransferCompleted,
            TestIconEventType.PairRequest => IconEventType.PairRequest,
            TestIconEventType.Notification => IconEventType.Notification,
            _ => IconEventType.Notification
        };
        AddEvent(type, $"[Test] {msg.Description}");
    }

    private void OnConfigurationChanged(object? sender, EventArgs e)
    {
        // Update icon visibility when configuration changes
        IsOpen = _configService.Current.ShowSpheneIcon;
    }
    
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _configService.ConfigSave -= OnConfigurationChanged;
            _spheneLogoTexture?.Dispose();
            _logger.LogDebug("SpheneIcon disposed");
        }
        base.Dispose(disposing);
    }
}
