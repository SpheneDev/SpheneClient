using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
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

namespace Sphene.UI;

public class SpheneIcon : WindowMediatorSubscriberBase, IDisposable
{
    private readonly ILogger<SpheneIcon> _logger;
    private readonly SpheneMediator _mediator;
    private readonly SpheneConfigService _configService;
    private readonly UiSharedService _uiSharedService;
    private readonly ApiController _apiController;
    
    private Vector2 _iconPosition = new Vector2(100, 100);
    private bool _hasStoredIconPosition = false;
    private bool _isDragging = false;
    private bool _wasClicked = false;
    private Vector2 _clickStartPos;
    private IDalamudTextureWrap? _mareLogoTexture;
    
    
    
    public SpheneIcon(ILogger<SpheneIcon> logger, SpheneMediator mediator, 
        SpheneConfigService configService, UiSharedService uiSharedService, ApiController apiController, PerformanceCollectorService performanceCollectorService) 
        : base(logger, mediator, "###SpheneIcon", performanceCollectorService)
    {
        _logger = logger;
        _mediator = mediator;
        _configService = configService;
        _uiSharedService = uiSharedService;
        _apiController = apiController;
        
        LoadIconPositionFromConfig();
        
        // Load Mare Logo Texture
        LoadMareLogoTexture();
        
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
        
        _logger.LogDebug("SpheneIcon created at position {Position}", _iconPosition);
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
        
        // Draw Mare Logo or fallback
        if (_mareLogoTexture != null)
        {
            drawList.AddImage(_mareLogoTexture.Handle, iconPos, iconPos + new Vector2(iconSize, iconSize));
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
            ImGui.BeginTooltip();
            ImGui.Text("Click to toggle Sphene | Hold and drag to move");
            ImGui.Separator();
            ImGui.Text($"Server Status: {GetStatusText(_apiController.ServerState)}");
            ImGui.EndTooltip();
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
    
    private void LoadMareLogoTexture()
    {
        try
        {
            if (!string.IsNullOrEmpty(SpheneImages.SpheneLogoBase64))
            {
                var imageData = Convert.FromBase64String(SpheneImages.SpheneLogoBase64);
                _mareLogoTexture = _uiSharedService.LoadImage(imageData);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Mare logo texture");
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
        _mareLogoTexture?.Dispose();
        _logger.LogDebug("SpheneIcon disposed");
    }
}