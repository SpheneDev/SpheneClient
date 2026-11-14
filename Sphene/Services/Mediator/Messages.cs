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
using Microsoft.Extensions.Logging;

namespace Sphene.Services.Mediator;

#pragma warning disable MA0048 // File name must match type name
#pragma warning disable S2094
public record SwitchToIntroUiMessage : MessageBase;
public record SwitchToIdentitySetupUiMessage : MessageBase;
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
public record ConnectedMessage(ConnectionDto Connection, bool ForceCharacterDataReload = false) : MessageBase;
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
public record MoodlesMessage(IntPtr Address, string? MoodlesData = null) : MessageBase;
public record PetNamesReadyMessage : MessageBase;
public record PetNamesMessage(string PetNicknamesData) : MessageBase;
public record HonorificReadyMessage : MessageBase;
public record BypassEmoteReadyMessage : MessageBase;
public record LifestreamTravelStateChangedMessage(LifestreamTravelStatus Status) : MessageBase;
public record BypassEmoteMessage(string BypassEmoteData) : MessageBase;
public record BypassEmoteUpdateMessage(string BypassEmoteData, string DataHash) : MessageBase;
public record PairBypassEmoteReceivedMessage(UserData Sender, string BypassEmoteData) : MessageBase;
public record TransientResourceChangedMessage(IntPtr Address) : MessageBase;
public record HaltScanMessage(string Source) : MessageBase;
public record ResumeScanMessage(string Source) : MessageBase;
public record NotificationMessage
    (string Title, string Message, NotificationType Type, TimeSpan? TimeShownOnScreen = null,
    /// <summary>Skip the session inbox row - used when the publisher already
    /// maintains its own inbox entry (chat unread rows).</summary>
    bool SuppressInbox = false) : MessageBase;
public record PlayNotificationSoundTestMessage(NotificationSoundConfig Config) : SameThreadMessage;
public record CreateCacheForObjectMessage(GameObjectHandler ObjectToCreateFor) : SameThreadMessage;
public record ClearCacheForObjectMessage(GameObjectHandler ObjectToCreateFor) : SameThreadMessage;
public record CharacterDataCreatedMessage(CharacterData CharacterData) : SameThreadMessage;
public record CharacterDataBuildStartedMessage : MessageBase;
public record CharacterDataBuildNoChangesMessage(string? DataHash = null) : MessageBase;
public record CharacterDataApplicationCompletedMessage(string PlayerName, string UserUID, Guid ApplicationId, bool Success, string? DataHash,
    Sphene.API.Dto.User.AcknowledgmentErrorCode ErrorCode = Sphene.API.Dto.User.AcknowledgmentErrorCode.None,
    string? ErrorMessage = null) : MessageBase;
public record CharacterDataAnalyzedMessage : MessageBase;
public record PenumbraStartRedrawMessage(IntPtr Address) : MessageBase;
public record PenumbraEndRedrawMessage(IntPtr Address) : MessageBase;
public record HubReconnectingMessage(Exception? Exception) : SameThreadMessage;
public record HubReconnectedMessage(string? Arg) : SameThreadMessage;
public record HubClosedMessage(Exception? Exception) : SameThreadMessage;
public record DebugLogEventMessage(LogLevel Level, string Category, string Message, string? Uid = null, string? Details = null) : SameThreadMessage;
public record DownloadReadyMessage(Guid RequestId) : MessageBase;
public record DownloadStartedMessage(GameObjectHandler DownloadId, Dictionary<string, FileDownloadStatus> DownloadStatus) : MessageBase;
public record DownloadFinishedMessage(GameObjectHandler DownloadId) : MessageBase;
public record UiToggleMessage(Type UiType) : MessageBase;
public record QueryWindowOpenStateMessage(Type UiType, Action<bool> Respond) : SameThreadMessage;
public record OpenSyncshellImageViewerMessage(List<SyncshellGalleryImageDto> Images, int StartIndex, string GroupGid) : MessageBase;

/// <summary>What the photo studio is editing for - fixes crop aspect, hints and defaults.</summary>
public enum PhotoStudioTarget
{
    /// <summary>Wide community page banner.</summary>
    CommunityBanner,
    /// <summary>Square community avatar.</summary>
    CommunityAvatar,
    /// <summary>Free-form gallery photo.</summary>
    CommunityPhoto,
    /// <summary>Own profile picture.</summary>
    ProfileAvatar,
    /// <summary>Own profile banner.</summary>
    ProfileBanner,
    /// <summary>Pair-list row background strip.</summary>
    ProfileRowImage,
    /// <summary>Free-form photo for the own profile gallery.</summary>
    ProfileGalleryPhoto,
    /// <summary>3:1 event banner (1200×400) - the event detail header.</summary>
    EventBanner,
    /// <summary>16:9 event preview image (800×450) - list and card contexts.</summary>
    EventPreview,
}

/// <summary>Opens the photo studio. A null <paramref name="FilePath"/> starts in the
/// built-in file browser; a path jumps straight into the editor. <paramref name="OnPrepared"/>
/// runs once the user confirms - it receives the encoded upload payload.</summary>
public record OpenPhotoStudioMessage(string? FilePath, PhotoStudioTarget Target,
    Action<Sphene.Utils.ImageUploadProcessor.Prepared> OnPrepared) : MessageBase;
public record PlayerUploadingMessage(GameObjectHandler Handler, bool IsUploading) : MessageBase;
public record ClearProfileDataMessage(UserData? UserData = null) : MessageBase;
public record CyclePauseMessage(UserData UserData) : MessageBase;
public record PauseMessage(UserData UserData) : MessageBase;
public record ProfilePopoutToggle(Pair? Pair) : SameThreadMessage;
public record ProfilePopoutPreload(Pair? Pair) : SameThreadMessage;
public record CompactUiChange(Vector2 Size, Vector2 Position) : SameThreadMessage;
public record RemoveWindowMessage(WindowMediatorSubscriberBase Window) : MessageBase;
public record RefreshUiMessage : MessageBase;
public record StructuralRefreshUiMessage : MessageBase;
public record OpenBanUserPopupMessage(Pair PairToBan, GroupFullInfoDto GroupFullInfoDto) : MessageBase;
public record OpenCensusPopupMessage() : MessageBase;
public record OpenTextureConversionMessage : MessageBase;
public record RequestSpheneReconnectMessage : MessageBase;
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
public record ChatMessageReceivedMessage(ChatMessageDto Message) : MessageBase;

public enum SpheneHubPage
{
    Profile = 0,
    Friends = 1,
    Syncshells = 2,
    CharacterData = 3,
    ModSharing = 4,
    ProfileStyle = 5,
    Settings = 6,
    Home = 7,
    DataAnalysis = 9,
    Supporter = 10,
    Housing = 11,
    News = 12,
    Community = 13
}
public enum SyncshellHubSubPage
{
    List = 0,
    Join = 1,
    Create = 2
}

/// <summary>Friend-detail tab - matches <see cref="Sphene.UI.Hub.SpheneHubUi"/>'s
/// friend tab order (Profile/Gallery/Chronicles/Chat/Activity). Used by
/// <see cref="OpenSpheneHubMessage.FriendSub"/> to deep-link into one tab.</summary>
public enum FriendHubSubPage
{
    Profile = 0,
    Gallery = 1,
    Chronicles = 2,
    Chat = 3,
    Activity = 4
}
public record OpenSpheneHubMessage(SpheneHubPage? Page = null, string? GroupGid = null, Pair? Friend = null, bool EditProfile = false, SyncshellHubSubPage? SyncshellSub = null,
    /// <summary>Prefill target for the Join subpage - discovery CTAs hand the
    /// shell gid straight to the join flow instead of making users retype it.</summary>
    string? JoinGid = null,
    /// <summary>Community deep-link - lands on the shell's community page
    /// opened at this section (and scrolled to <see cref="CommunityItemId"/>).</summary>
    SyncshellCommunitySection? CommunitySection = null, long? CommunityItemId = null,
    /// <summary>Syncshell detail-tab deep-link - <see cref="Sphene.UI.Hub.SpheneHubUi"/>'s
    /// internal tab enum as int (Community = 0 …, EventPlanner = 5); the hub maps
    /// it when <paramref name="GroupGid"/> is set.</summary>
    int? DetailTab = null,
    /// <summary>Event-Planner deep-link - opens this event in the planner
    /// editor once the community cache lands; the optional occurrence start
    /// scopes the edit to one series date.</summary>
    long? EditEventId = null, SyncshellEventOccurrenceDto? EditOccurrence = null,
    /// <summary>Friend-detail deep-link - opens <paramref name="Friend"/> on
    /// this tab instead of the default Profile tab.</summary>
    FriendHubSubPage? FriendSub = null) : MessageBase;

/// <summary>The user opened a DM conversation - unread surfaces (inbox row,
/// badges) for that peer should clear.</summary>
public record ChatConversationOpenedMessage(string OtherUid) : MessageBase;

/// <summary>The caller's syncshell bookmarks changed - the Followed surface
/// reloads instead of holding a stale list.</summary>
public record SyncshellFavoritesChangedMessage : MessageBase;

/// <summary>Opens the Event Planner live-preview popout for the draft -
/// the window binds the composer, so edits update the preview live and
/// nothing is saved or sent. <paramref name="Community"/> carries the host's
/// effective community aggregate (banner, tabs, gallery, the saved event)
/// so the preview can render the real attendee page with the draft
/// overlaid; <paramref name="GroupInfo"/> supplies the full group record
/// for hero identity where the host has one.</summary>
internal record OpenEventPreviewMessage(GroupData Group,
    Sphene.API.Dto.Group.SyncshellCommunityDto Community,
    Sphene.API.Dto.Group.GroupFullInfoDto? GroupInfo,
    Sphene.UI.Syncshell.SyncshellCommunityView.CommunityEventComposer Composer) : MessageBase;

/// <summary>Opens the Chronicle Live Preview bound to the editor's draft.
/// The window binds the <see cref="Sphene.UI.Chronicles.ChronicleComposer"/>
/// so unsaved edits update the preview live; the author name/accent are
/// snapshot at open so the preview mirrors the published identity.</summary>
internal record OpenChroniclePreviewMessage(
    Sphene.UI.Chronicles.ChronicleComposer Composer, string AuthorUid,
    string AuthorDisplayName, System.Numerics.Vector4 Accent) : MessageBase;
public record DownloadLimitChangedMessage() : SameThreadMessage;
public record CensusUpdateMessage(byte Gender, byte RaceId, byte TribeId) : MessageBase;
public record TargetPairMessage(Pair Pair) : MessageBase;
public record CombatOrPerformanceStartMessage : MessageBase;
public record CombatOrPerformanceEndMessage : MessageBase;
public record EventMessage(Event Event) : MessageBase;
public record PenumbraDirectoryChangedMessage(string? ModDirectory) : MessageBase;
public record PenumbraRedrawCharacterMessage(ICharacter Character) : SameThreadMessage;

public enum TestIconEventType
{
    ModTransferAvailable,
    ModTransferCompleted,
    PairRequest,
    Notification
}

public record TestIconEventMessage(TestIconEventType EventType, string Description) : MessageBase;
public record GameObjectHandlerCreatedMessage(GameObjectHandler GameObjectHandler, bool OwnedObject) : SameThreadMessage;
public record GameObjectHandlerDestroyedMessage(GameObjectHandler GameObjectHandler, bool OwnedObject) : SameThreadMessage;
public record HaltCharaDataCreation(bool Resume = false) : SameThreadMessage;
public record GposeLobbyUserJoin(UserData UserData) : MessageBase;
public record GPoseLobbyUserLeave(UserData UserData) : MessageBase;
public record GPoseLobbyReceiveCharaData(CharaDataDownloadDto CharaDataDownloadDto) : MessageBase;
public record GPoseLobbyReceivePoseData(UserData UserData, PoseData PoseData) : MessageBase;
public record GPoseLobbyReceiveWorldData(UserData UserData, WorldData WorldData) : MessageBase;
public record CharacterDataRefreshRequestedMessage(UserData Requester) : MessageBase;
public record RequestRemoteCharacterDataRefreshMessage(UserData Target, string? DataHash, string? Reason) : MessageBase;
public record CharacterDataReceivedForPairMessage(Pair Pair, Sphene.API.Data.CharacterData? CharacterData) : MessageBase;
public record OpenCharaDataHubWithFilterMessage(UserData UserData) : MessageBase;
public record SendCharacterDataAcknowledgmentMessage(CharacterDataAcknowledgmentDto AcknowledgmentDto) : MessageBase;
public record ShowUpdateNotificationMessage(UpdateInfo UpdateInfo) : MessageBase;
public record CheckForUpdatesMessage : MessageBase;
public record ShowReleaseChangelogMessage(string CurrentVersion, string? ChangelogText, string? LastSeenVersionBeforeUpdate = null) : MessageBase;
public record ReleaseChangelogClosedMessage : MessageBase;
public record UiServiceInitializedMessage : MessageBase;

public record ThemePickerModeToggleMessage(bool IsEnabled) : MessageBase;
public record ThemeNavigateToButtonSettingsMessage(string ButtonStyleKey) : MessageBase;

public record CompactUiStickToSettingsMessage(bool Enabled, Vector2 SettingsPos, Vector2 SettingsSize) : SameThreadMessage;

public record OpenPairRequestsUiMessage : SameThreadMessage;

// Server announcements · publish pushes carry the DTO for the toast,
// changed asks every cache to invalidate.
public record AnnouncementPublishedMessage(Sphene.API.Dto.Announcement.AnnouncementDto Announcement) : MessageBase;
public record AnnouncementsChangedMessage : MessageBase;

// Community content changed on a syncshell · caches for that shell invalidate.
public record CommunityChangedMessage(Sphene.API.Dto.Group.CommunityChangedDto Community) : MessageBase;

// A durable user notification was pushed by the server.
public record UserNotificationReceivedMessage(Sphene.API.Dto.User.UserNotificationDto Notification) : MessageBase;

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
