using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Textures.TextureWraps;
using Sphene.API.Data;
using Sphene.API.Dto;
using Sphene.API.Dto.CharaData;
using Sphene.API.Dto.Group;
using Sphene.API.Dto.User;
using Sphene.SpheneConfiguration.Models;
using Sphene.PlayerData.Handlers;
using Sphene.PlayerData.Pairs;
using Sphene.Services.Events;
using Sphene.Services;
using Sphene.WebAPI.Files.Models;
using Sphene.API.Dto.Files;
using System.Numerics;

namespace Sphene.Services.Mediator;

#pragma warning disable MA0048 // File name must match type name
#pragma warning disable S2094
public record SwitchToIntroUiMessage : MessageBase;
public record SwitchToMainUiMessage : MessageBase;
public record OpenSettingsUiMessage : MessageBase;
public record DalamudLoginMessage : MessageBase;
public record DalamudLogoutMessage : MessageBase;
public record PriorityFrameworkUpdateMessage : SameThreadMessage;
public record FrameworkUpdateMessage : SameThreadMessage;
public record ClassJobChangedMessage(GameObjectHandler GameObjectHandler) : MessageBase;
public record DelayedFrameworkUpdateMessage : SameThreadMessage;
public record ZoneSwitchStartMessage : MessageBase;
public record ZoneSwitchEndMessage : MessageBase;
public record DutyStartMessage : MessageBase;
public record DutyEndMessage : MessageBase;
public record CutsceneStartMessage : MessageBase;
public record GposeStartMessage : SameThreadMessage;
public record GposeEndMessage : MessageBase;
public record CutsceneEndMessage : MessageBase;
public record CutsceneFrameworkUpdateMessage : SameThreadMessage;
public record ConnectedMessage(ConnectionDto Connection) : MessageBase;
public record DisconnectedMessage : SameThreadMessage;
public record PenumbraModSettingChangedMessage : MessageBase;
public record PenumbraInitializedMessage : MessageBase;
public record PenumbraDisposedMessage : MessageBase;
public record PenumbraRedrawMessage(IntPtr Address, int ObjTblIdx, bool WasRequested) : SameThreadMessage;
public record GlamourerChangedMessage(IntPtr Address) : MessageBase;
public record HeelsOffsetMessage : MessageBase;
public record PenumbraResourceLoadMessage(IntPtr GameObject, string GamePath, string FilePath) : SameThreadMessage;
public record CustomizePlusMessage(nint? Address) : MessageBase;
public record HonorificMessage(string NewHonorificTitle) : MessageBase;
public record MoodlesMessage(IntPtr Address) : MessageBase;
public record PetNamesReadyMessage : MessageBase;
public record PetNamesMessage(string PetNicknamesData) : MessageBase;
public record HonorificReadyMessage : MessageBase;
public record TransientResourceChangedMessage(IntPtr Address) : MessageBase;
public record HaltScanMessage(string Source) : MessageBase;
public record ResumeScanMessage(string Source) : MessageBase;
public record NotificationMessage
    (string Title, string Message, NotificationType Type, TimeSpan? TimeShownOnScreen = null) : MessageBase;
public record CreateCacheForObjectMessage(GameObjectHandler ObjectToCreateFor) : SameThreadMessage;
public record ClearCacheForObjectMessage(GameObjectHandler ObjectToCreateFor) : SameThreadMessage;
public record CharacterDataCreatedMessage(CharacterData CharacterData) : SameThreadMessage;
public record CharacterDataBuildStartedMessage : MessageBase;
public record CharacterDataApplicationCompletedMessage(string PlayerName, string UserUID, Guid ApplicationId, bool Success) : MessageBase;
public record CharacterDataAnalyzedMessage : MessageBase;
public record PenumbraStartRedrawMessage(IntPtr Address) : MessageBase;
public record PenumbraEndRedrawMessage(IntPtr Address) : MessageBase;
public record HubReconnectingMessage(Exception? Exception) : SameThreadMessage;
public record HubReconnectedMessage(string? Arg) : SameThreadMessage;
public record HubClosedMessage(Exception? Exception) : SameThreadMessage;
public record DownloadReadyMessage(Guid RequestId) : MessageBase;
public record DownloadStartedMessage(GameObjectHandler DownloadId, Dictionary<string, FileDownloadStatus> DownloadStatus) : MessageBase;
public record DownloadFinishedMessage(GameObjectHandler DownloadId) : MessageBase;
public record UiToggleMessage(Type UiType) : MessageBase;
public record QueryWindowOpenStateMessage(Type UiType, Action<bool> Respond) : SameThreadMessage;
public record OpenWelcomePageLivePreviewMessage(GroupFullInfoDto GroupFullInfo, string WelcomeText, IDalamudTextureWrap? WelcomeImageTexture) : MessageBase;
public record UpdateWelcomePageLivePreviewMessage(string WelcomeText, IDalamudTextureWrap? WelcomeImageTexture) : MessageBase;
public record PlayerUploadingMessage(GameObjectHandler Handler, bool IsUploading) : MessageBase;
public record ClearProfileDataMessage(UserData? UserData = null) : MessageBase;
public record CyclePauseMessage(UserData UserData) : MessageBase;
public record PauseMessage(UserData UserData) : MessageBase;
public record ProfilePopoutToggle(Pair? Pair) : MessageBase;
public record CompactUiChange(Vector2 Size, Vector2 Position) : MessageBase;
public record ProfileOpenStandaloneMessage(Pair Pair) : MessageBase;
public record RemoveWindowMessage(WindowMediatorSubscriberBase Window) : MessageBase;
public record RefreshUiMessage : MessageBase;
public record StructuralRefreshUiMessage : MessageBase;
public record OpenBanUserPopupMessage(Pair PairToBan, GroupFullInfoDto GroupFullInfoDto) : MessageBase;
public record OpenCensusPopupMessage() : MessageBase;
public record OpenPenumbraModInstallPopupMessage(FileTransferNotificationDto Notification) : MessageBase;
public record InstallReceivedPenumbraModMessage(FileTransferNotificationDto Notification) : SameThreadMessage;
public record PenumbraModTransferAvailableMessage(FileTransferNotificationDto Notification) : MessageBase;
public record PenumbraModTransferCompletedMessage(FileTransferNotificationDto Notification, bool Success) : MessageBase;
public record PenumbraModTransferDiscardedMessage(FileTransferNotificationDto Notification) : MessageBase;
public record FileTransferAckMessage(string Hash, string SenderUID) : MessageBase;
public record PenumbraModTransferProgressMessage(FileTransferNotificationDto Notification, string Status, float? Progress) : SameThreadMessage;
public record OpenPenumbraReceiveModWindow(List<FileTransferNotificationDto> Notifications) : MessageBase;
public record OpenSyncshellAdminPanel(GroupFullInfoDto GroupInfo) : MessageBase;
public record OpenPermissionWindow(Pair Pair) : MessageBase;
public record OpenSendPenumbraModWindow(Pair? Pair, string? PreselectedModFolderName = null) : MessageBase;
public enum ModSharingTab
{
    Send = 0,
    Receive = 1,
    History = 2
}
public record OpenModSharingWindow(ModSharingTab Tab) : MessageBase;
public record DownloadLimitChangedMessage() : SameThreadMessage;
public record CensusUpdateMessage(byte Gender, byte RaceId, byte TribeId) : MessageBase;
public record TargetPairMessage(Pair Pair) : MessageBase;
public record CombatOrPerformanceStartMessage : MessageBase;
public record CombatOrPerformanceEndMessage : MessageBase;
public record EventMessage(Event Event) : MessageBase;
public record PenumbraDirectoryChangedMessage(string? ModDirectory) : MessageBase;
public record PenumbraRedrawCharacterMessage(ICharacter Character) : SameThreadMessage;
public record GameObjectHandlerCreatedMessage(GameObjectHandler GameObjectHandler, bool OwnedObject) : SameThreadMessage;
public record GameObjectHandlerDestroyedMessage(GameObjectHandler GameObjectHandler, bool OwnedObject) : SameThreadMessage;
public record HaltCharaDataCreation(bool Resume = false) : SameThreadMessage;
public record GposeLobbyUserJoin(UserData UserData) : MessageBase;
public record GPoseLobbyUserLeave(UserData UserData) : MessageBase;
public record GPoseLobbyReceiveCharaData(CharaDataDownloadDto CharaDataDownloadDto) : MessageBase;
public record GPoseLobbyReceivePoseData(UserData UserData, PoseData PoseData) : MessageBase;
public record GPoseLobbyReceiveWorldData(UserData UserData, WorldData WorldData) : MessageBase;
public record OpenCharaDataHubWithFilterMessage(UserData UserData) : MessageBase;
public record SendCharacterDataAcknowledgmentMessage(CharacterDataAcknowledgmentDto AcknowledgmentDto) : MessageBase;
public record ShowUpdateNotificationMessage(UpdateInfo UpdateInfo) : MessageBase;
public record CheckForUpdatesMessage : MessageBase;
public record ShowReleaseChangelogMessage(string CurrentVersion, string? ChangelogText, string? LastSeenVersionBeforeUpdate = null) : MessageBase;
public record UiServiceInitializedMessage : MessageBase;

public record ThemePickerModeToggleMessage(bool IsEnabled) : MessageBase;
public record ThemeNavigateToButtonSettingsMessage(string ButtonStyleKey) : MessageBase;

public record CompactUiStickToSettingsMessage(bool Enabled, Vector2 SettingsPos, Vector2 SettingsSize) : SameThreadMessage;

// Area-bound syncshell messages
public record AreaBoundJoinRequestMessage(AreaBoundJoinRequestDto JoinRequest) : MessageBase;
public record AreaBoundJoinResponseMessage(AreaBoundJoinResponseDto JoinResponse) : MessageBase;
public record AreaBoundLocationChangedMessage(LocationInfo NewLocation, LocationInfo? PreviousLocation) : MessageBase;
public record AreaBoundSyncshellNotificationMessage(string Title, string Message, NotificationLocation Location) : SameThreadMessage;
public record AreaBoundSyncshellConfigurationUpdateMessage : MessageBase;
public record AreaBoundSyncshellConsentRequestMessage(AreaBoundSyncshellDto Syncshell, bool RequiresRulesAcceptance) : SameThreadMessage;
public record AreaBoundSyncshellSelectionRequestMessage(List<AreaBoundSyncshellDto> AvailableSyncshells) : SameThreadMessage;
public record AreaBoundSyncshellLeftMessage(string SyncshellId) : MessageBase;
#pragma warning restore S2094
#pragma warning restore MA0048 // File name must match type name
