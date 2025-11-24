using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using Microsoft.Extensions.Logging;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Hosting;
using Sphene.Services.Mediator;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sphene.Services;

public class CharacterStatusService : MediatorSubscriberBase, IHostedService
{
    private readonly IClientState _clientState;
    private readonly ICondition _condition;
    private readonly IDataManager _dataManager;

    private static readonly uint[] AcceptedOnlineStatusIds = { 0, 32, 31, 27, 28, 29, 30, 12, 17, 21, 22, 23 };

    // Online status constants
    public const uint OnlineStatusOnline = 0;
    public const uint OnlineStatusAway = 17;
    public const uint OnlineStatusBusy = 12;
    public const uint OnlineStatusRolePlay = 22;
    public const uint OnlineStatusLookingToMeld = 23;
    public const uint OnlineStatusLookingForParty = 21;
    public const uint OnlineStatusNewAdventurer = 32;
    public const uint OnlineStatusReturning = 31;
    public const uint OnlineStatusMentor = 27;
    public const uint OnlineStatusBattleMentor = 28;
    public const uint OnlineStatusPvPMentor = 30;
    public const uint OnlineStatusTradeMentor = 29;

    public CharacterStatusService(ILogger<CharacterStatusService> logger, IClientState clientState, 
        ICondition condition, IDataManager dataManager, SpheneMediator mediator) : base(logger, mediator)
    {
        _clientState = clientState;
        _condition = condition;
        _dataManager = dataManager;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Logger.LogInformation("CharacterStatusService started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Logger.LogInformation("CharacterStatusService stopped");
        return Task.CompletedTask;
    }

    // Get current online status ID
    public uint GetCurrentOnlineStatusId()
    {
        return _clientState.LocalPlayer?.OnlineStatus.RowId ?? OnlineStatusOnline;
    }

    // Get current online status name
    public string GetCurrentOnlineStatusName()
    {
        var statusId = GetCurrentOnlineStatusId();
        return GetStatusName(statusId);
    }

    // Check if a status ID is valid/accepted
    public static bool IsValidOnlineStatusId(uint statusId)
    {
        if (statusId == 0) return true;
        
        return AcceptedOnlineStatusIds.Contains(statusId);
    }

    // Get all available online statuses with validation info
    public Dictionary<uint, string> GetAvailableOnlineStatuses()
    {
        var result = new Dictionary<uint, string>();
        var onlineStatusSheet = _dataManager.GetExcelSheet<OnlineStatus>();
        
        if (onlineStatusSheet == null) return result;

        foreach (var statusId in AcceptedOnlineStatusIds)
        {
            try
            {
                var status = onlineStatusSheet.GetRow(statusId);
                var statusName = status.Name.ToString() ?? $"Status {statusId}";
                if (IsMentorStatus(statusId))
                {
                    statusName += " (Requires mentor unlock)";
                }
                result[statusId] = statusName;
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Failed to read online status name for id {statusId}", statusId);
            }
        }

        return result;
    }

    // Check if a status is a mentor status
    public static bool IsMentorStatus(uint statusId)
    {
        return statusId == OnlineStatusMentor || 
               statusId == OnlineStatusBattleMentor || 
               statusId == OnlineStatusPvPMentor || 
               statusId == OnlineStatusTradeMentor;
    }

    // Get available statuses with unlock validation
    public unsafe Dictionary<uint, (string Name, bool IsUnlocked)> GetAvailableOnlineStatusesWithValidation()
    {
        var result = new Dictionary<uint, (string Name, bool IsUnlocked)>();
        var onlineStatusSheet = _dataManager.GetExcelSheet<OnlineStatus>();
        
        if (onlineStatusSheet == null) return result;

        // Get PlayerState for mentor status validation
        unsafe
        {
            PlayerState* playerState = PlayerState.Instance();

            foreach (var statusId in AcceptedOnlineStatusIds)
            {
                try
                {
                    var status = onlineStatusSheet.GetRow(statusId);
                    var statusName = status.Name.ToString() ?? $"Status {statusId}";
                    if (statusId == 0)
                    {
                        statusName = "Online";
                    }
                    bool isUnlocked = true;
                    if (IsMentorStatus(statusId) && playerState != null)
                    {
                        isUnlocked = statusId switch
                        {
                            OnlineStatusMentor => playerState->IsBattleMentor() && playerState->IsTradeMentor() && playerState->MentorVersion == 3,
                            OnlineStatusBattleMentor => playerState->IsBattleMentor() && playerState->MentorVersion == 3,
                            OnlineStatusPvPMentor => playerState->IsBattleMentor() && playerState->MentorVersion == 3,
                            OnlineStatusTradeMentor => playerState->IsTradeMentor() && playerState->MentorVersion == 3,
                            _ => false
                        };
                    }
                    result[statusId] = (statusName, isUnlocked);
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Failed to read online status validation for id {statusId}", statusId);
                }
            }
        }

        return result;
    }

    // Set online status
    public unsafe bool SetOnlineStatus(uint statusId)
    {
        try
        {
            // Check if we're bound by duty
            if (_condition[ConditionFlag.BoundByDuty56])
            {
                Logger.LogWarning("Cannot change online status while bound by duty");
                return false;
            }

            // Validate status ID
            if (!IsValidOnlineStatusId(statusId))
            {
                Logger.LogWarning("Invalid online status ID: {statusId}", statusId);
                return false;
            }

            // Get InfoProxyDetail and send status update
            var infoProxyDetail = InfoProxyDetail.Instance();
            if (infoProxyDetail == null)
            {
                Logger.LogError("InfoProxyDetail instance is null");
                return false;
            }

            // Special handling for "Looking for Party" status - set job mask
            if (statusId == OnlineStatusLookingForParty && infoProxyDetail->UpdateData.LookingForPartyClassJobIdMask == 0)
            {
                var jobId = _clientState.LocalPlayer?.ClassJob.RowId ?? 0;
                if (jobId > 0)
                {
                    var jobMask = (ulong)(1u << ((int)jobId - 1));
                    Logger.LogDebug("Setting job mask for Looking for Party status: JobId={jobId}, JobMask={jobMask}", jobId, jobMask);
                    
                    // Set the current job ID and job mask for Looking for Party status
                    infoProxyDetail->SetUpdateClassJobId((byte)jobId);
                    infoProxyDetail->SetUpdateLookingForPartyClassJobIdMask(jobMask);
                }
            }

            infoProxyDetail->SendOnlineStatusUpdate(statusId);
            Logger.LogInformation("Online status changed to: {statusId} ({statusName})", statusId, GetStatusName(statusId));
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to set online status to {statusId}", statusId);
            return false;
        }
    }

    // Convenience methods for common status changes
    public bool SetOnline() => SetOnlineStatus(OnlineStatusOnline);
    public bool SetAway() => SetOnlineStatus(OnlineStatusAway);
    public bool SetBusy() => SetOnlineStatus(OnlineStatusBusy);
    public bool SetRolePlay() => SetOnlineStatus(OnlineStatusRolePlay);
    public bool SetLookingForParty() => SetOnlineStatus(OnlineStatusLookingForParty);

    // Status check methods
    public bool IsOnline() => GetCurrentOnlineStatusId() == OnlineStatusOnline;
    public bool IsAway() => GetCurrentOnlineStatusId() == OnlineStatusAway;
    public bool IsBusy() => GetCurrentOnlineStatusId() == OnlineStatusBusy;
    public bool IsRolePlay() => GetCurrentOnlineStatusId() == OnlineStatusRolePlay;
    public bool IsLookingForParty() => GetCurrentOnlineStatusId() == OnlineStatusLookingForParty;

    public string GetStatusName(uint statusId)
    {
        // Handle special case for status ID 0 - set name to "Online"
        if (statusId == 0)
        {
            return "Online";
        }
        
        var onlineStatus = _dataManager.GetExcelSheet<OnlineStatus>()?.GetRow(statusId);
        return onlineStatus?.Name.ToString() ?? $"Status {statusId}";
    }
}
