using System;
using System.Numerics;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;
using Microsoft.Extensions.Logging;
using Sphene.API.Dto;
using Sphene.Services;
using Sphene.Services.Mediator;

namespace Sphene.UI;

public class DeathrollInvitationUI : WindowMediatorSubscriberBase
{
    private readonly ILogger<DeathrollInvitationUI> _logger;
    private readonly DeathrollService _deathrollService;
    
    private DeathrollInvitationDto? _currentInvitation;
    private DateTime _invitationReceivedAt = DateTime.MinValue;
    private readonly TimeSpan _autoCloseDelay = TimeSpan.FromSeconds(30);

    public DeathrollInvitationUI(
        ILogger<DeathrollInvitationUI> logger,
        SpheneMediator mediator,
        DeathrollService deathrollService,
        PerformanceCollectorService performanceCollectorService) : base(logger, mediator, "Deathroll Invitation", performanceCollectorService)
    {
        _logger = logger;
        _deathrollService = deathrollService;
        
        WindowName = "Deathroll Invitation";
        Size = new Vector2(400, 200);
        SizeCondition = ImGuiCond.Always;
        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize;
        
        // Subscribe to invitation messages
        Mediator.Subscribe<DeathrollInvitationReceivedMessage>(this, OnInvitationReceived);
        
        _logger.LogDebug("DeathrollInvitationUI initialized");
    }

    protected override void DrawInternal()
    {
        if (_currentInvitation == null)
        {
            IsOpen = false;
            return;
        }

        // Auto-close after delay
        if (DateTime.UtcNow - _invitationReceivedAt > _autoCloseDelay)
        {
            _logger.LogDebug("Auto-closing invitation window after timeout");
            CloseInvitation();
            return;
        }

        DrawInvitationContent();
    }

    private void DrawInvitationContent()
    {
        if (_currentInvitation == null) return;

        // Header
        ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.2f, 1.0f), "Deathroll Invitation");
        ImGui.Separator();

        // Sender info
        ImGui.Text($"From: {_currentInvitation.Sender.AliasOrUID}");
        
        // Message
        ImGui.TextWrapped(_currentInvitation.Message);
        
        // Time remaining
        var timeRemaining = _currentInvitation.ExpiresAt - DateTime.UtcNow;
        if (timeRemaining.TotalSeconds > 0)
        {
            ImGui.Text($"Expires in: {timeRemaining:mm\\:ss}");
        }
        else
        {
            ImGui.TextColored(new Vector4(1.0f, 0.2f, 0.2f, 1.0f), "Invitation expired");
        }

        ImGui.Separator();

        // Buttons
        var buttonSize = new Vector2(80, 25);
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var buttonSpacing = 10f;
        var totalButtonWidth = (buttonSize.X * 2) + buttonSpacing;
        var startX = (availableWidth - totalButtonWidth) * 0.5f;

        ImGui.SetCursorPosX(startX);
        
        // Accept button
        if (ImGui.Button("Accept", buttonSize))
        {
            AcceptInvitation();
        }
        
        ImGui.SameLine(0, buttonSpacing);
        
        // Decline button
        if (ImGui.Button("Decline", buttonSize))
        {
            DeclineInvitation();
        }

        // Close button (X) handling
        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            DeclineInvitation();
        }
    }

    private void OnInvitationReceived(DeathrollInvitationReceivedMessage message)
    {
        _logger.LogInformation("DEATHROLL DEBUG: UI received deathroll invitation from {sender} - InvitationId: {invitationId}", 
            message.Invitation.Sender.AliasOrUID, message.Invitation.InvitationId);
        
        try
        {
            _currentInvitation = message.Invitation;
            _invitationReceivedAt = DateTime.UtcNow;
            IsOpen = true;
            
            // Bring window to front
            ImGui.SetNextWindowFocus();
            
            _logger.LogInformation("DEATHROLL DEBUG: UI invitation window opened successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DEATHROLL DEBUG: Failed to open invitation UI window");
        }
    }

    private void AcceptInvitation()
    {
        if (_currentInvitation == null) return;

        _logger.LogDebug("Accepting deathroll invitation from {sender}", _currentInvitation.Sender.AliasOrUID);
        
        // Send acceptance through the deathroll service
        _ = _deathrollService.AcceptInvitationAsync(_currentInvitation);
        
        // Open the Deathroll Lobby UI and select the Current Lobby tab
        Mediator.Publish(new OpenDeathrollLobbyMessage(SelectCurrentLobbyTab: true));
        
        CloseInvitation();
    }

    private void DeclineInvitation()
    {
        if (_currentInvitation == null) return;

        _logger.LogDebug("Declining deathroll invitation from {sender}", _currentInvitation.Sender.AliasOrUID);
        
        // Send decline through the deathroll service
        _ = _deathrollService.DeclineInvitationAsync(_currentInvitation);
        
        CloseInvitation();
    }

    private void CloseInvitation()
    {
        _currentInvitation = null;
        _invitationReceivedAt = DateTime.MinValue;
        IsOpen = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _currentInvitation = null;
        }
        base.Dispose(disposing);
    }
}

// Message for when a deathroll invitation is received
public record DeathrollInvitationReceivedMessage(DeathrollInvitationDto Invitation) : MessageBase;

// Message for when a deathroll invitation response is received
public record DeathrollInvitationResponseMessage(DeathrollInvitationResponseDto Response) : MessageBase;

// Message for when a deathroll game state update is received
public record DeathrollGameStateUpdateMessage(DeathrollGameStateDto GameState) : MessageBase;

// Message for when a deathroll lobby join request is received
public record DeathrollLobbyJoinRequestMessage(string LobbyId, string PlayerName) : MessageBase;

// Message for when a deathroll lobby is opened/closed
public record DeathrollLobbyOpenCloseMessage(string LobbyId, bool IsOpen) : MessageBase;

// Message for when a deathroll game starts from lobby
public record DeathrollGameStartMessage(string LobbyId) : MessageBase;

// Message for when a deathroll lobby is canceled
public record DeathrollLobbyCanceledMessage(string LobbyId) : MessageBase;

// Message for when a player leaves a lobby
public record DeathrollLobbyLeaveMessage(string LobbyId, string PlayerName) : MessageBase;

// Message for when a player ready status changes
public record DeathrollPlayerReadyMessage(string LobbyId, string PlayerId, bool IsReady) : MessageBase;

// Message for when a lobby announcement is received
public record DeathrollLobbyAnnouncementMessage(string LobbyId, string Message, string? HostName = null) : MessageBase;

// Message for when a deathroll tournament state update is received
public record DeathrollTournamentStateUpdateMessage(DeathrollTournamentStateDto TournamentState) : MessageBase;