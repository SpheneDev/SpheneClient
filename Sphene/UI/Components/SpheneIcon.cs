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
using Sphene.UI.Design;
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
using Sphene.UI.Hub;

namespace Sphene.UI.Components;

public class SpheneIcon : WindowMediatorSubscriberBase
{
    private new readonly ILogger<SpheneIcon> _logger;
    private readonly SpheneMediator _mediator;
    private readonly SpheneConfigService _configService;
    private readonly ApiController _apiController;
    private readonly IpcManager _ipcManager;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ICommandManager _commandManager;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly ShrinkUHostService _shrinkuHostService;
    private readonly NotificationInboxService _notificationInbox;
    private readonly Sphene.PlayerData.Pairs.PairManager _pairManager;
    // Built-in ShrinkU integration
    private readonly ShrinkU.Configuration.ShrinkUConfigService _shrinkuConfig;
    private readonly ShrinkU.UI.ConversionUI _shrinkuConversion;
    private readonly ShrinkU.UI.FirstRunSetupUI _shrinkuFirstRun;
    private float _bubbleT;
    private float _menuT;
    
    private Vector2 _iconPosition = new Vector2(100, 100);
    private bool _hasStoredIconPosition = false;
    private bool _isDragging = false;
    private bool _wasClicked = false;
    private Vector2 _clickStartPos;
    private Vector2 _dragStartMousePos;
    private Vector2 _dragStartIconPos;
    private DateTime _mouseDownAt;
    private DateTime _dragRingFadeStart;
    private bool _showDragRingFade = false;
    private const int DragRingFadeDurationMs = 250;
    private Vector2 _lastSavedIconPosition = new(float.NaN, float.NaN);
    private const int DragStartDelayMs = 350; // delay (ms) before drag begins on hold
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
        SpheneConfigService configService, ApiController apiController, 
        PerformanceCollectorService performanceCollectorService, IpcManager ipcManager, IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager, DalamudUtilService dalamudUtilService, ShrinkUHostService shrinkuHostService,
        ShrinkU.Configuration.ShrinkUConfigService shrinkuConfig,
        ShrinkU.UI.ConversionUI shrinkuConversion,
        ShrinkU.UI.SettingsUI shrinkuSettings,
        ShrinkU.UI.FirstRunSetupUI shrinkuFirstRun,
        NotificationInboxService notificationInbox,
        Sphene.PlayerData.Pairs.PairManager pairManager)
        : base(logger, mediator, "###SpheneIcon", performanceCollectorService)
    {
        _logger = logger;
        _mediator = mediator;
        _configService = configService;
        _apiController = apiController;
        _ipcManager = ipcManager;
        _pluginInterface = pluginInterface;
        _commandManager = commandManager;
        _dalamudUtilService = dalamudUtilService;
        _shrinkuHostService = shrinkuHostService;
        _notificationInbox = notificationInbox;
        _pairManager = pairManager;
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

        // Subscribe to GPose messages to properly hide/show icon
        Mediator.Subscribe<GposeStartMessage>(this, (_) =>
        {
            if (_configService.Current.HideSpheneIconInGpose)
            {
                IsOpen = false;
                _logger.LogDebug("SpheneIcon hidden on GPose start");
            }
        });
        Mediator.Subscribe<GposeEndMessage>(this, (_) =>
        {
            if (_configService.Current.HideSpheneIconInGpose)
            {
                IsOpen = _configService.Current.ShowSpheneIcon;
                _logger.LogDebug("SpheneIcon restored on GPose end. IsOpen={IsOpen}", IsOpen);
            }
        });

        _logger.LogDebug("SpheneIcon created at position {Position}", _iconPosition);
    }
    
    /// <summary>
    /// Right-click quick panel: status header, pending inbox shortcut,
    /// app jump-off points (Hub / CompactUI / Settings / Downloads), the
    /// compatible-plugin grid, icon options and community links - all in the
    /// segmented design language instead of raw menu items.
    /// </summary>
    /// <summary>
    /// Right-click regulator menu · a compact flyout that grows straight out
    /// of the icon's right edge (the mark tilts away while open). Two-column
    /// plugin grid keeps the panel short; app destinations are icon-only so
    /// nothing clips.
    /// </summary>
    /// Inbox mode inside the icon menu · click "pending" swaps the panel's
    /// content instead of opening a second window; back shrinks it again.
    private bool _menuShowsInbox;

    /// <summary>
    /// Inbox content swapped into the icon-menu panel · the panel widens
    /// (flyout auto-resizes), caps at the menu's height and scrolls past
    /// that. The back arrow returns to the quick menu view.
    /// </summary>
    private void DrawMenuInbox(float w, float listH)
    {
        // back + title on one line · back is the only exit besides closing
        if (SpheneUI.Icons.Button(FontAwesomeIcon.ArrowLeft))
        {
            _menuShowsInbox = false;
        }
        SpheneUI.AttachTooltip("Back to menu");
        ImGui.SameLine();
        SpheneUI.Text.Colored("Pending", SpheneUI.Colors.TextPrimary);
        var pending = NotificationInbox.PendingCount(_notificationInbox, _pairManager);
        ImGui.SameLine();
        SpheneUI.Text.Timestamp($"{pending} items");
        SpheneUI.Layout.Separator();

        // fixed list height handed in by the panel · more items scroll.
        if (pending == 0)
        {
            SpheneUI.Layout.EmptyState(FontAwesomeIcon.BellSlash,
                "All caught up", "Pair requests, transfers and updates land here.");
        }
        else if (SpheneUI.Layout.BeginScrollArea("menu-inbox-scroll",
                new Vector2(w, listH)))
        {
            NotificationInbox.Draw(_notificationInbox, _pairManager,
                _apiController, Mediator);
            SpheneUI.Layout.EndScrollArea();
        }
    }

    private void DrawContextMenu()
    {
        if (!SpheneUI.Popups.BeginFlyout("icon-menu"))
        {
            _menuShowsInbox = false;
            // snap the width tween back to menu size · otherwise a closed
            // inbox-mode tween reopens the panel at inbox width.
            SpheneUI.Fx.TweenSet("icon-menu:w", 272f * SpheneUI.Scale);
            return;
        }
        var scale = SpheneUI.Scale;
        var accent = SpheneUI.Colors.Accent;
        var pending = NotificationInbox.PendingCount(_notificationInbox, _pairManager);
        var gap = ImGui.GetStyle().ItemSpacing.X;
        var closeMenu = false;

        // fixed geometry · the panel is exactly 272px wide in the menu and
        // 304px in the inbox, PanelH tall in both - a dummy pads whatever
        // the view didn't fill, nothing is left to auto-measurement.
        // pending button row (~30) + its separator (~10) drops out when
        // nothing is pending - the panel shrinks by exactly that much.
        var panelH = pending > 0 ? 384f : 344f;
        var w = SpheneUI.Fx.Tween("icon-menu:w",
            _menuShowsInbox ? 304f * scale : 272f * scale);
        var boundMin = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(w, 0f));
        SpheneUI.ContentBounds.Push(
            new Vector2(boundMin.X, boundMin.X + w));
        if (_menuShowsInbox)
        {
            DrawMenuInbox(w, panelH * scale - 48f * scale);
        }
        else
        {

        // Status strip · dot + name + server state, one line
        var statusColor = GetStatusColor(_apiController.ServerState);
        SpheneUI.Icons.Icon(FontAwesomeIcon.Circle, statusColor);
        ImGui.SameLine();
        SpheneUI.Text.Colored("Sphene", SpheneUI.Colors.TextPrimary);
        ImGui.SameLine();
        SpheneUI.Text.Timestamp(GetStatusText(_apiController.ServerState));

        if (pending > 0)
        {
            // fixed to the tweened width · a full-avail button would hold
            // the auto-resize flyout at the wider inbox size on the way back.
            if (SpheneUI.Controls.SegmentButton(
                    $"{pending} pending · inbox###icon-open-inbox",
                    FontAwesomeIcon.Bell, size: new Vector2(w, 0f),
                    accent: SpheneUI.Palette.SpheneGold))
            {
                // swap this panel into the inbox instead of a second window
                _menuShowsInbox = true;
            }
            SpheneUI.Layout.Separator();
        }

        // App jump-off · one row of four icon buttons, tooltips carry names
        var appH = 30f * scale;
        var appW = (w - gap * 3f) * 0.25f;
        if (SpheneUI.Icons.Button(FontAwesomeIcon.Home, appH, null, null, appW))
        {
            _mediator.Publish(new UiToggleMessage(typeof(SpheneHubUi)));
            closeMenu = true;
        }
        SpheneUI.AttachTooltip("Sphene Hub");
        ImGui.SameLine(0f, gap);
        if (SpheneUI.Icons.Button(FontAwesomeIcon.List, appH, null, null, appW))
        {
            ToggleMainWindow();
            closeMenu = true;
        }
        SpheneUI.AttachTooltip("Control Panel");
        ImGui.SameLine(0f, gap);
        if (SpheneUI.Icons.Button(FontAwesomeIcon.Cog, appH, null, null, appW))
        {
            _mediator.Publish(new UiToggleMessage(typeof(SettingsUi)));
            closeMenu = true;
        }
        SpheneUI.AttachTooltip("Settings");
        ImGui.SameLine(0f, gap);
        if (SpheneUI.Icons.Button(FontAwesomeIcon.ExchangeAlt, appH, null, null, appW))
        {
            _mediator.Publish(new OpenSpheneHubMessage(SpheneHubPage.ModSharing));
            closeMenu = true;
        }
        SpheneUI.AttachTooltip("Mod Share");

        SpheneUI.Layout.Separator();
        SpheneUI.Text.SectionHeader("Quick Access");

        // Two-column plugin grid · names only (the "Open" prefix stole ~35px),
        // ellipsized to the cell so nothing overflows the panel edge
        var plugins = GetAvailablePlugins();
        var colW = (w - gap) * 0.5f;
        var cell = new Vector2(colW, 30f * scale);
        for (var i = 0; i < plugins.Count; i++)
        {
            var plugin = plugins[i];
            var isShrinkU = string.Equals(plugin.InternalName, "ShrinkU.Builtin",
                StringComparison.OrdinalIgnoreCase);
            var enabled = !isShrinkU || _configService.Current.EnableShrinkUIntegration;
            DrawPluginCell(plugin, cell, accent, enabled, ref closeMenu);
            if (i % 2 == 0 && i + 1 < plugins.Count)
            {
                ImGui.SameLine(0f, gap);
            }
        }
        if (plugins.Count == 0)
        {
            SpheneUI.Text.Muted("No compatible plugins found");
        }

        SpheneUI.Layout.Separator();

        var locked = _configService.Current.LockSpheneIcon;
        if (SpheneUI.Controls.Checkbox("Lock position###icon-lock", ref locked))
        {
            _configService.Current.LockSpheneIcon = locked;
            _configService.Save();
        }
        ImGui.SameLine();
        var bubble = _configService.Current.ShowIconNotificationBubble;
        if (SpheneUI.Controls.Checkbox("Bubble###icon-bubble", ref bubble))
        {
            _configService.Current.ShowIconNotificationBubble = bubble;
            _configService.Save();
        }
        SpheneUI.AttachTooltip("Notification bubble on the icon");

        SpheneUI.Layout.Separator();
        LinkChips.Chip("Discord", "https://discord.gg/GbnwsP2XsF", "discord", accent, 1);
        ImGui.SameLine();
        LinkChips.Chip("Ko-fi", "https://ko-fi.com/sphene", "kofi",
            SpheneUI.Palette.SpheneGold, 2);
        }   // menu content

        // pad the shorter view out to the fixed panel height
        ImGui.Dummy(new Vector2(0f, MathF.Max(0f,
            boundMin.Y + panelH * scale - ImGui.GetCursorScreenPos().Y)));
        SpheneUI.ContentBounds.Pop();
        if (closeMenu)
        {
            SpheneUI.Popups.CloseFlyout("icon-menu");
        }
        SpheneUI.Popups.EndFlyout();
    }

    /// <summary>
    /// One plugin cell in the Quick Access grid - the plugin's real logo
    /// cover-fills the left icon zone (rounded on the leading edge), name
    /// ellipsized next to it. Hover lifts the whole cell like the link cards.
    /// </summary>
    private void DrawPluginCell(PluginInfo plugin, Vector2 cell, Vector4 accent,
        bool enabled, ref bool closeMenu)
    {
        var scale = SpheneUI.Scale;
        var dl = ImGui.GetWindowDrawList();
        ImGui.PushID($"plcell-{plugin.InternalName}");
        var clicked = ImGui.InvisibleButton("##cell", cell);
        var hovered = enabled && ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var max = min + cell;
        var radius = 8f * scale;
        var id = ImGui.GetID("##cell");
        var t = SpheneUI.Fx.HoverT(id, hovered);

        var fill = SpheneUI.Layout.NavRailBandColor();
        fill = SpheneUI.ColorUtils.WithAlpha(fill,
            MathF.Min(1f, fill.W + 0.08f * t));
        dl.AddRectFilled(min, max, SpheneUI.ColorUtils.ToU32(fill), radius);
        dl.AddRect(min, max, SpheneUI.ColorUtils.ToU32(
            SpheneUI.ColorUtils.WithAlpha(
                hovered ? accent : SpheneUI.Colors.Border,
                hovered ? 0.55f : 0.3f)), radius);

        // logo zone · square on the cell's left edge, logo cover-fills it
        var side = cell.Y;
        var logoMax = min + new Vector2(side, side);
        var wrap = SphenePluginIcons.Texture(plugin.InternalName);
        var alpha = enabled ? 1f : 0.45f;
        if (wrap != null)
        {
            // logo conforms to the cell's rounding - rounded image with the
            // square right corners masked by the cell clip.
            dl.PushClipRect(min, max, true);
            SpheneUI.Media.ImageRoundedInRect(dl, wrap, min, logoMax,
                radius, new Vector4(1f, 1f, 1f, alpha));
            dl.PopClipRect();
            // hairline frame around the logo zone · barely-there border,
            // easing to a soft accent edge on hover
            var logoRim = SpheneUI.ColorUtils.WithAlpha(
                hovered ? accent : SpheneUI.Colors.Border,
                hovered ? 0.5f : 0.35f);
            dl.AddRect(min, logoMax, SpheneUI.ColorUtils.ToU32(logoRim), radius,
                ImDrawFlags.RoundCornersLeft);
            dl.AddLine(new Vector2(logoMax.X + 0.5f, min.Y + 4f * scale),
                new Vector2(logoMax.X + 0.5f, max.Y - 4f * scale),
                SpheneUI.ColorUtils.ToU32(
                    SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.Border, 0.3f)));
        }
        else
        {
            // drawlist-level glyph render - NOT an ImGui item, so the 2-col
            // SameLine pairing keeps the cell rect as "last item" (an item
            // here would steal the same-line anchor and overlap the next cell).
            var glyph = SphenePluginIcons.FallbackGlyph(plugin.InternalName);
            var glyphStr = FontAwesomeExtensions.ToIconString(glyph);
            var gi = ImGui.CalcTextSize(glyphStr);
            dl.AddText(UiBuilder.IconFont, gi.Y,
                min + (new Vector2(side, side) - gi) * 0.5f,
                SpheneUI.ColorUtils.ToU32(
                    SpheneUI.ColorUtils.WithAlpha(
                        hovered ? accent : SpheneUI.Colors.TextMuted, alpha)),
                glyphStr);
        }

        // label · vertically centered next to the logo, ellipsized to fit
        var label = SpheneUI.Text.Ellipsize(
            plugin.Name, cell.X - side - 8f * scale);
        var th = ImGui.GetTextLineHeight();
        dl.AddText(new Vector2(logoMax.X + 6f * scale, min.Y + (cell.Y - th) * 0.5f),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
                SpheneUI.Colors.TextPrimary, alpha)), label);

        if (clicked && enabled)
        {
            OpenPlugin(plugin.InternalName);
            closeMenu = true;
        }
        if (ImGui.IsItemHovered())
        {
            SpheneUI.AttachTooltip(enabled
                ? plugin.Name : "Enable ShrinkU integration first");
        }
        ImGui.PopID();
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
    
    protected override bool UseSpheneChrome => false;

    protected override void DrawInternal()
    {
        var cfg = _configService.Current;

        // Hide icon in GPose mode if configured
        if (cfg.HideSpheneIconInGpose && _dalamudUtilService.IsInGpose)
        {
            return;
        }

        var iconSize = 32f;
        var padding = 4f;
        var windowSize = iconSize + padding * 2;

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

        // Set window size to just the icon area (effects render on foreground drawlist outside window)
        ImGui.SetWindowSize(new Vector2(windowSize, windowSize), ImGuiCond.Always);

        // Get current window position
        var currentPos = ImGui.GetWindowPos();

        // Prune expired events before drawing
        PruneExpiredEvents();

        // Use foreground drawlist for all visual elements so they overlay properly
        // above the small transparent interaction window
        var drawList = ImGui.GetForegroundDrawList();

        // Draw the Sphene logo/icon
        var baseIconPos = new Vector2(currentPos.X + padding, currentPos.Y + padding);
        var iconPos = new Vector2(baseIconPos.X + scaledOffset, baseIconPos.Y + scaledOffset);
        var iconAlpha = cfg.IconGlobalAlpha;
        var iconColorVec = SpheneUI.Palette.SpheneGold;
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
        // Draw effects (behind icon) on foreground drawlist so they are not clipped
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

        // Regulator menu tilt · while the quick menu grows out of the
        // icon's right edge, the mark leans away from it (counterclockwise),
        // like the regulator swivelling to project the panel.
        var menuOpen = SpheneUI.Popups.IsFlyoutOpen("icon-menu");
        var mdt = ImGui.GetIO().DeltaTime;
        _menuT = Math.Clamp(_menuT + (menuOpen ? mdt : -mdt) * 6f, 0f, 1f);
        var menuT = _menuT * _menuT * (3f - 2f * _menuT);
        // the menu mirrors to the right when the icon sits near the left
        // screen edge - the tilt follows the same direction.
        var menuOnRight = iconPos.X - 280f * SpheneUI.Scale < 4f;
        var tilt = (menuOnRight ? -0.28f : 0.28f) * menuT;

        // Draw Sphene Logo or fallback
        var logoTexture = _spheneLogoTexture ??= SpheneBrandIcons.SpheneLogo();
        if (logoTexture != null)
        {
            SpheneUI.Media.ImageQuadInRect(drawList, logoTexture, iconPos,
                iconPos + new Vector2(scaledIconSize, scaledIconSize), tilt,
                new Vector4(1, 1, 1, iconAlpha));
        }
        else
        {
            // Fallback: Draw a simple circle as icon
            drawList.AddCircleFilled(new Vector2(iconPos.X + scaledIconSize/2, iconPos.Y + scaledIconSize/2),
                scaledIconSize/2 - 2, iconColor);

            // Add "S" text in the center
            using (SpheneUI.UidFont.Push())
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

        // Notification bubble ("Sprechblase") · eases in top-right of the
        // icon while inbox items or pair requests are pending. Click opens
        // the inbox flyout anchored to the bubble.
        var pending = NotificationInbox.PendingCount(_notificationInbox, _pairManager);
        var bubbleOn = cfg.ShowIconNotificationBubble && pending > 0;
        var dt = ImGui.GetIO().DeltaTime;
        _bubbleT = Math.Clamp(_bubbleT + (bubbleOn ? dt : -dt) * 8f, 0f, 1f);
        var bubbleT = _bubbleT * _bubbleT * (3f - 2f * _bubbleT);
        var bubbleHovered = false;
        if (bubbleT > 0.01f)
        {
            var countText = pending > 9 ? "9+" : pending.ToString();
            var countW = ImGui.CalcTextSize(countText).X;
            var bubbleW = (countW + 18f) * bubbleT;
            var bubbleH = 17f;
            var iconRight = iconPos.X + scaledIconSize;
            var bubbleMax = new Vector2(iconRight + bubbleW * 0.45f, iconPos.Y - 1f);
            var bubbleMin = bubbleMax - new Vector2(bubbleW, bubbleH);

            var bubbleFill = SpheneUI.ColorUtils.WithAlpha(
                SpheneUI.Palette.EtherealPurple, 0.92f * iconAlpha * bubbleT);
            drawList.AddRectFilled(bubbleMin, bubbleMax,
                ImGui.ColorConvertFloat4ToU32(bubbleFill), bubbleH * 0.5f);
            drawList.AddRect(bubbleMin, bubbleMax,
                ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.5f * iconAlpha * bubbleT)),
                bubbleH * 0.5f, ImDrawFlags.None, 1f);

            // speech tail · little triangle pointing down-left at the icon
            var tailBase = new Vector2(bubbleMin.X + bubbleW * 0.25f, bubbleMax.Y);
            drawList.AddTriangleFilled(
                tailBase, tailBase + new Vector2(9f, 0f), tailBase + new Vector2(2f, 6f),
                ImGui.ColorConvertFloat4ToU32(bubbleFill));

            if (bubbleW > countW + 8f)
            {
                var textPos = new Vector2(
                    bubbleMin.X + (bubbleW - countW) * 0.5f,
                    bubbleMin.Y + (bubbleH - ImGui.GetTextLineHeight()) * 0.5f);
                drawList.AddText(textPos,
                    ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, iconAlpha * bubbleT)),
                    countText);
            }

            var mouse = ImGui.GetIO().MousePos;
            bubbleHovered = bubbleT > 0.9f
                && mouse.X >= bubbleMin.X && mouse.X <= bubbleMax.X
                && mouse.Y >= bubbleMin.Y && mouse.Y <= bubbleMax.Y;
            if (bubbleHovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    if (SpheneUI.Popups.IsFlyoutOpen("icon-inbox"))
                    {
                        SpheneUI.Popups.CloseFlyout("icon-inbox");
                    }
                    else
                    {
                        // same anchor the quick menu uses · icon rect
                        SpheneUI.Popups.OpenFlyout("icon-inbox", iconPos);
                    }
                }
            }
        }

        // Inbox flyout · shared pending-items list (pair requests inline
        // actions, transfers, updates, area-bound consents). EndFlyout only
        // pairs with a successful BeginFlyout - an unmatched End pops style
        // vars it never pushed (native crash).
        if (SpheneUI.Popups.BeginFlyout("icon-inbox"))
        {
            var flyW = 300f * SpheneUI.Scale;
            // height follows content · pair rows ~46px, inbox rows ~40px,
            // capped at 320px before scrolling kicks in.
            var itemCount = PairRequestsSection.PendingCount(_pairManager)
                + _notificationInbox.GetItems().Count;
            // same height as the quick menu so both panels read as one set -
            // more items just scroll inside it.
            var flyH = Math.Clamp(itemCount * 58f * SpheneUI.Scale
                + SpheneUI.Spacing.X12.Scaled, 0f, 240f * SpheneUI.Scale);
            // a 0-height scroll area means "fill remaining" and grows the
            // auto-resize flyout forever · draw the empty state directly.
            if (itemCount == 0)
            {
                ImGui.Dummy(new Vector2(flyW, 0f));
                SpheneUI.Layout.EmptyState(FontAwesomeIcon.BellSlash,
                    "All caught up",
                    "Pair requests, transfers and updates land here.");
            }
            else if (SpheneUI.Layout.BeginScrollArea("icon-inbox-scroll",
                    new Vector2(flyW, flyH)))
            {
                NotificationInbox.Draw(_notificationInbox, _pairManager,
                    _apiController, Mediator);
                SpheneUI.Layout.EndScrollArea();
            }
            SpheneUI.Popups.EndFlyout();
        }

        // Draw hold-progress ring when mouse is held and drag is not locked
        if (!iconLocked && ImGui.IsMouseDown(ImGuiMouseButton.Left) && (_wasClicked || _isDragging))
        {
            var elapsedMs = (DateTime.UtcNow - _mouseDownAt).TotalMilliseconds;
            var progress = _isDragging
                ? 1.0
                : Math.Min(Math.Max((elapsedMs - 200) / (DragStartDelayMs - 200.0), 0.0), 1.0);

            if (progress > 0 || _isDragging)
            {
                var center = new Vector2(iconPos.X + scaledIconSize / 2, iconPos.Y + scaledIconSize / 2);
                var radius = scaledIconSize / 2 + 4f;
                var ringColor = ImGui.ColorConvertFloat4ToU32(SpheneUI.Palette.EtherealPurple with { W = 0.9f * iconAlpha });
                var bgRingColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.12f * iconAlpha));

                // Background ring
                drawList.AddCircle(center, radius, bgRingColor, 64, 2f);

                // Progress arc
                var startAngle = -MathF.PI / 2; // Start at top
                var endAngle = startAngle + (float)(progress * 2 * Math.PI);

                drawList.PathArcTo(center, radius, startAngle, endAngle, 64);
                drawList.PathStroke(ringColor, ImDrawFlags.None, 2f);
            }
        }

        // Fade out the ring after dragging ends
        if (_showDragRingFade)
        {
            var fadeElapsedMs = (DateTime.UtcNow - _dragRingFadeStart).TotalMilliseconds;
            if (fadeElapsedMs < DragRingFadeDurationMs)
            {
                var fade = 1.0f - (float)(fadeElapsedMs / DragRingFadeDurationMs);
                var center = new Vector2(iconPos.X + scaledIconSize / 2, iconPos.Y + scaledIconSize / 2);
                var radius = scaledIconSize / 2 + 4f;
                var ringColor = ImGui.ColorConvertFloat4ToU32(SpheneUI.Palette.EtherealPurple with { W = 0.9f * iconAlpha * fade });

                drawList.PathArcTo(center, radius, -MathF.PI / 2, -MathF.PI / 2 + MathF.PI * 2, 64);
                drawList.PathStroke(ringColor, ImDrawFlags.None, 2f);
            }
            else
            {
                _showDragRingFade = false;
            }
        }

        // Handle dragging and clicking only for the icon area (rest of window is click-through)
        ImGui.SetCursorPos(new Vector2(padding, padding));
        ImGui.InvisibleButton("##sphene_icon_window", new Vector2(iconSize, iconSize));
        // Right-click · the regulator menu grows straight out of the icon's
        // right edge via the flyout anchor (item rect = icon rect here).
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            SpheneUI.Popups.ToggleFlyout("icon-menu");
        }

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
        if (!iconLocked && _wasClicked && ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            var elapsedMs = (DateTime.UtcNow - _mouseDownAt).TotalMilliseconds;
            var currentMouse = ImGui.GetMousePos();

            if (!_isDragging && elapsedMs >= DragStartDelayMs)
            {
                _isDragging = true;
                _dragStartMousePos = currentMouse;
                _dragStartIconPos = ImGui.GetWindowPos();
            }

            if (_isDragging)
            {
                var delta = new Vector2(currentMouse.X - _dragStartMousePos.X, currentMouse.Y - _dragStartMousePos.Y);
                var newPos = new Vector2(_dragStartIconPos.X + delta.X, _dragStartIconPos.Y + delta.Y);
                // Dalamud applies Position right before Begin · never
                // mid-frame · so the window and its draw commands land
                // at the new spot together
                Position = newPos;
                PositionCondition = ImGuiCond.Always;
                _iconPosition = newPos;
            }
        }

        // Handle mouse release (using !IsMouseDown instead of IsItemDeactivated
        // so the release is detected even if the cursor leaves the item area)
        if (_wasClicked && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            if (_isDragging)
            {
                // Save position when drag ends · Position is the dragged
                // target already applied before Begin
                _isDragging = false;
                _iconPosition = Position ?? ImGui.GetWindowPos();
                _hasStoredIconPosition = true;
                SaveIconPositionToConfig();
                _showDragRingFade = true;
                _dragRingFadeStart = DateTime.UtcNow;
                _logger.LogDebug("Icon position saved: {Position}", _iconPosition);
            }
            else
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
            using (SpheneUI.Theme.TooltipScope())
            {
                ImGui.BeginTooltip();
                SpheneUI.Text.Plain(_tooltipHeaderText);
                ImGui.Separator();
                SpheneUI.Text.Plain(_tooltipStatusText);
                if (_updateAvailable && _updateInfo != null && !string.IsNullOrEmpty(_tooltipUpdateText))
                {
                    ImGui.Separator();
                    SpheneUI.Text.Plain(_tooltipUpdateText);
                }
                if (_cachedEventDescriptions.Count > 0)
                {
                    ImGui.Separator();
                    foreach (var description in _cachedEventDescriptions)
                    {
                        SpheneUI.Text.Plain(description);
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
            : "Click to toggle Sphene | Hold for drag (0.5 s) | Right-click for menu";
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
    
    private static void LoadSpheneLogoTexture()
    {
        // Texture resolves asynchronously through the media cache; the draw
        // path renders the circle fallback until the wrap is available.
        _ = SpheneBrandIcons.SpheneLogo();
    }
    
    private void DrawStatusIndicator(ImDrawListPtr drawList, Vector2 iconPos, float iconSize, float alpha = 1.0f)
    {
        var indicatorRadius = 4f;
        var indicatorPos = new Vector2(
            iconPos.X + iconSize - indicatorRadius - 2f,
            iconPos.Y + indicatorRadius + 2f
        );

        var statusColor = _apiController.IsTransientDisconnectInProgress
            ? Sphene.UI.Design.SpheneTheme.Current.CompactServerStatusWarning
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
        var t = Sphene.UI.Design.SpheneTheme.Current;
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

    private static void DrawGlow(ImDrawListPtr drawList, Vector2 iconPos, float iconSize,
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

        // Alpha fades out quadratically as ring expands · full at start, gone at outer edge
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

    private static (uint Color, float Alpha, bool EffectPulse, bool EffectGlow, bool EffectBounce, bool EffectRainbow,
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
                    _ => SpheneUI.Palette.SpheneGold
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
        // If currently in GPose and hide-in-GPose is enabled, keep icon hidden
        if (_dalamudUtilService.IsInGpose && _configService.Current.HideSpheneIconInGpose)
        {
            IsOpen = false;
        }
        else
        {
            IsOpen = _configService.Current.ShowSpheneIcon;
        }
    }
    
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _configService.ConfigSave -= OnConfigurationChanged;
            _logger.LogDebug("SpheneIcon disposed");
        }
        base.Dispose(disposing);
    }
}
