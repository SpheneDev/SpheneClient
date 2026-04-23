using Sphene.SpheneConfiguration.Models;
using Sphene.UI;
using Sphene.UI.Components;
using Microsoft.Extensions.Logging;
using Sphene.API.Data;
using Sphene.API.Dto.CharaData;
using Sphene.API.Dto.Files;
using Sphene.API.Dto.Group;

namespace Sphene.SpheneConfiguration.Configurations;

[Serializable]
public class SpheneConfig : ISpheneConfiguration
{
    public bool AcceptedAgreement { get; set; } = false;
    public string CacheFolder { get; set; } = string.Empty;
    public bool DisableOptionalPluginWarnings { get; set; } = false;
    public bool EnableDtrEntry { get; set; } = false;
    public bool ShowUidInDtrTooltip { get; set; } = true;
    public bool PreferNoteInDtrTooltip { get; set; } = false;
    public bool UseColorsInDtr { get; set; } = true;
    public DtrEntry.Colors DtrColorsDefault { get; set; } = default;
    public DtrEntry.Colors DtrColorsNotConnected { get; set; } = new(Glow: 0x0428FFu);
    public DtrEntry.Colors DtrColorsPairsInRange { get; set; } = new(Glow: 0xFFBA47u);
    public bool EnableRightClickMenus { get; set; } = true;
    public NotificationLocation ErrorNotification { get; set; } = NotificationLocation.Both;
    public string ExportFolder { get; set; } = string.Empty;
    public bool FileScanPaused { get; set; } = false;
    public NotificationLocation InfoNotification { get; set; } = NotificationLocation.Toast;
    public bool InitialScanComplete { get; set; } = false;
    public LogLevel LogLevel { get; set; } = LogLevel.Information;
    public bool LogPerformance { get; set; } = false;
    public double MaxLocalCacheInGiB { get; set; } = 20;
    public bool OpenGposeImportOnGposeStart { get; set; } = false;
    public bool OpenPopupOnAdd { get; set; } = true;
    public int ParallelDownloads { get; set; } = 10;
    public int DownloadSpeedLimitInBytes { get; set; } = 0;
    public DownloadSpeeds DownloadSpeedType { get; set; } = DownloadSpeeds.MBps;
    public bool PreferNotesOverNamesForVisible { get; set; } = false;
    public float ProfileDelay { get; set; } = 1.5f;
    public bool ProfilePopoutRight { get; set; } = false;
    public bool ProfilesAllowNsfw { get; set; } = false;
    public bool ProfilesShow { get; set; } = true;
    public int MaxDisconnectPauseSeconds { get; set; } = 30; // Default 30 seconds
    public int MaxDeferredTimeoutSeconds { get; set; } = 300; // Default 5 minutes
    public bool ShowSyncshellUsersInVisible { get; set; } = true;
    public bool ShowVisibleSyncshellUsersOnlyInSyncshells { get; set; } = false;
    public bool ShowCharacterNameInsteadOfNotesForVisible { get; set; } = false;
    public bool ShowOfflineUsersSeparately { get; set; } = true;
    public bool ShowSyncshellOfflineUsersSeparately { get; set; } = true;
    public bool GroupUpSyncshells { get; set; } = true;
    public bool ShowOnlineNotifications { get; set; } = false;
    public bool ShowOnlineNotificationsOnlyForIndividualPairs { get; set; } = true;
    public bool ShowOnlineNotificationsOnlyForNamedPairs { get; set; } = false;
    
    // Area-bound syncshell notification settings
    public bool ShowAreaBoundSyncshellNotifications { get; set; } = true;
    public NotificationLocation AreaBoundSyncshellNotification { get; set; } = NotificationLocation.Toast;
    public bool ShowAreaBoundSyncshellWelcomeMessages { get; set; } = true;
    public bool AutoShowAreaBoundSyncshellConsent { get; set; } = true;
    
    // City syncshell settings
    public bool EnableCitySyncshellJoinRequests { get; set; } = true;
    public bool HasSeenCitySyncshellExplanation { get; set; } = false;
    public bool HasSeenSyncshellSettings { get; set; } = false;
    
    public bool ShowTransferBars { get; set; } = true;
    public bool ShowTransferWindow { get; set; } = false;
    public bool ShowUploading { get; set; } = true;
    public bool ShowUploadingBigText { get; set; } = true;
    public bool ShowVisibleUsersSeparately { get; set; } = true;
    public bool AllowReceivingPenumbraMods { get; set; } = true;
    public bool UseSpheneCdnDirectDownloads { get; set; } = true;
    public int TimeSpanBetweenScansInSeconds { get; set; } = 30;
    public int TransferBarsHeight { get; set; } = 12;
    public bool TransferBarsShowText { get; set; } = true;
    public int TransferBarsWidth { get; set; } = 250;
    public bool UseAlternativeFileUpload { get; set; } = false;
    public bool UseCompactor { get; set; } = false;
    public bool DebugStopWhining { get; set; } = false;
    public bool AutoPopulateEmptyNotesFromCharaName { get; set; } = false;
    public int Version { get; set; } = 1;
    public NotificationLocation WarningNotification { get; set; } = NotificationLocation.Both;
    public bool UseFocusTarget { get; set; } = false;
    public float IconPositionX { get; set; } = 100f;
    public float IconPositionY { get; set; } = 100f;
    public bool ShowSpheneIcon { get; set; } = true;
    public bool LockSpheneIcon { get; set; } = false;
    public int IconEventExpirySeconds { get; set; } = 60;

    // Icon Theme Settings - Per-Event-Type Configuration (Flexible Effect System)
    // Each notification type can have its own color, alpha, and combination of effects

    // Global Settings (only rainbow speed is truly global)
    public float IconGlobalAlpha { get; set; } = 1.0f;
    public float IconRainbowSpeed { get; set; } = 1.0f;

    // Badge Visibility
    public bool IconShowModTransferBadge { get; set; } = true;
    public bool IconShowPairRequestBadge { get; set; } = true;
    public bool IconShowNotificationBadge { get; set; } = true;

    // === PER-EVENT-TYPE CONFIGURATION ===
    // Format: Prefix = EventType, Suffix = Property

    // PERMANENT (Background) Event
    public uint IconPermColor { get; set; } = 0xFFE86699u; // Purple (#9966E8)
    public float IconPermAlpha { get; set; } = 0.3f;
    public bool IconPermEffectPulse { get; set; } = true;
    public bool IconPermEffectGlow { get; set; } = false;
    public bool IconPermEffectBounce { get; set; } = false;
    public bool IconPermEffectRainbow { get; set; } = false;
    public float IconPermPulseMinRadius { get; set; } = 0.46f;
    public float IconPermPulseMaxRadius { get; set; } = 0.6f;
    public float IconPermGlowIntensity { get; set; } = 0.6f;
    public float IconPermGlowRadius { get; set; } = 1.2f;
    public float IconPermBounceIntensity { get; set; } = 0.12f;
    public float IconPermBounceSpeed { get; set; } = 1.5f;

    // MOD TRANSFER Event
    public uint IconModTransferColor { get; set; } = 0xFFE00060u; // Blue (#0060E0)
    public float IconModTransferAlpha { get; set; } = 0.6f;
    public bool IconModTransferEffectPulse { get; set; } = true;
    public bool IconModTransferEffectGlow { get; set; } = false;
    public bool IconModTransferEffectBounce { get; set; } = false;
    public bool IconModTransferEffectRainbow { get; set; } = false;
    public float IconModTransferPulseMinRadius { get; set; } = 0.46f;
    public float IconModTransferPulseMaxRadius { get; set; } = 0.6f;
    public float IconModTransferGlowIntensity { get; set; } = 0.6f;
    public float IconModTransferGlowRadius { get; set; } = 1.2f;
    public float IconModTransferBounceIntensity { get; set; } = 0.12f;
    public float IconModTransferBounceSpeed { get; set; } = 1.5f;

    // PAIR REQUEST Event
    public uint IconPairRequestColor { get; set; } = 0xFFB469FFu; // Pink (#FF69B4)
    public float IconPairRequestAlpha { get; set; } = 0.6f;
    public bool IconPairRequestEffectPulse { get; set; } = true;
    public bool IconPairRequestEffectGlow { get; set; } = false;
    public bool IconPairRequestEffectBounce { get; set; } = false;
    public bool IconPairRequestEffectRainbow { get; set; } = false;
    public float IconPairRequestPulseMinRadius { get; set; } = 0.46f;
    public float IconPairRequestPulseMaxRadius { get; set; } = 0.6f;
    public float IconPairRequestGlowIntensity { get; set; } = 0.6f;
    public float IconPairRequestGlowRadius { get; set; } = 1.2f;
    public float IconPairRequestBounceIntensity { get; set; } = 0.12f;
    public float IconPairRequestBounceSpeed { get; set; } = 1.5f;

    // NOTIFICATION (General) Event
    public uint IconNotificationColor { get; set; } = 0xFF0099FFu; // Orange (#FF9900)
    public float IconNotificationAlpha { get; set; } = 0.6f;
    public bool IconNotificationEffectPulse { get; set; } = true;
    public bool IconNotificationEffectGlow { get; set; } = false;
    public bool IconNotificationEffectBounce { get; set; } = false;
    public bool IconNotificationEffectRainbow { get; set; } = false;
    public float IconNotificationPulseMinRadius { get; set; } = 0.46f;
    public float IconNotificationPulseMaxRadius { get; set; } = 0.6f;
    public float IconNotificationGlowIntensity { get; set; } = 0.6f;
    public float IconNotificationGlowRadius { get; set; } = 1.2f;
    public float IconNotificationBounceIntensity { get; set; } = 0.12f;
    public float IconNotificationBounceSpeed { get; set; } = 1.5f;

    public float PenumbraSendPopupPosX { get; set; } = 0f;
    public float PenumbraSendPopupPosY { get; set; } = 0f;
    public bool PenumbraSendPopupUseCustomPosition { get; set; } = false;
    public float PenumbraReceivePopupPosX { get; set; } = 0f;
    public float PenumbraReceivePopupPosY { get; set; } = 0f;
    public bool PenumbraReceivePopupUseCustomPosition { get; set; } = false;
    // Enable or disable integration of ShrinkU UI inside Sphene
    public bool EnableShrinkUIntegration { get; set; } = true;
    public string PenumbraModDownloadFolder { get; set; } = string.Empty;
    public bool DeletePenumbraModAfterInstall { get; set; } = false;
    public List<FileTransferNotificationDto> PendingPenumbraModTransfers { get; set; } = new();

    // Acknowledgment System Settings
    public bool ShowAcknowledgmentPopups { get; set; } = false;
    public bool ShowWaitingForAcknowledgmentPopups { get; set; } = false;
    public bool EnableAcknowledgmentBatching { get; set; } = true;
    public bool EnableAcknowledgmentAutoRetry { get; set; } = true;
    public bool EnableDutyCombatSyncWithoutRedraw { get; set; } = false;
    public bool EnableDutyCombatOutgoingSyncBatching { get; set; } = false;
    public int DutyCombatOutgoingSyncBatchSeconds { get; set; } = 10;
    public bool FilterCharacterLegacyShpkInOutgoingCharacterData { get; set; } = false;

    public int AcknowledgmentTimeoutSeconds { get; set; } = 30;
    public NotificationLocation AcknowledgmentNotification { get; set; } = NotificationLocation.Toast;
    
    // Incognito Mode Settings
    public bool IsIncognitoModeActive { get; set; } = false;
    public HashSet<string> PrePausedPairs { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> PrePausedSyncshells { get; set; } = new(StringComparer.Ordinal);
    
    // Housing Ownership Settings
    public List<LocationInfo> OwnedHousingProperties { get; set; } = new();
    public List<VerifiedHousingProperty> VerifiedOwnedHousingProperties { get; set; } = new();
    public Dictionary<string, Dictionary<string, List<AreaBoundLocationDto>>> SuspendedAreaBoundLocationsByGroupAndProperty { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<byte>> HousingPlotSizesByTerritoryId { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, HousingInteriorFloorMapIds> HousingInteriorFloorMapIdsByTerritoryAndSize { get; set; } = new(StringComparer.Ordinal);
    
    // Theme Settings
    public string SelectedThemeName { get; set; } = "Default Sphene";
    public string SelectedTheme { get; set; } = "Default Sphene";
    public bool AutoLoadThemeOnStartup { get; set; } = true;

    // Release Changelog
    public string LastSeenVersionChangelog { get; set; } = string.Empty;
    public string LastSeenNewOptionsTag { get; set; } = string.Empty;
    public List<string> SeenNewOptionsTags { get; set; } = new();
    public string ReleaseChangelogUrl { get; set; } = string.Empty;
    public bool ShowTestBuildChangelogs { get; set; } = false;
    public bool ShowTestBuildUpdates { get; set; } = false;

    public bool UseTestServerOverride { get; set; } = false;
    public string TestServerApiUrl { get; set; } = string.Empty;
    public bool HasAcceptedTestServerDisclaimer { get; set; } = false;

    // Active Mismatch Tracker Filter Settings
    public bool MismatchTrackerTrackEquipmentPaths { get; set; } = false; // chara/weapon, chara/equipment, chara/accessory
    public bool MismatchTrackerTrackMinionMountAndPetPaths { get; set; } = false;
    public bool MismatchTrackerTrackPhybFiles { get; set; } = true;
    public bool MismatchTrackerTrackSkpFiles { get; set; } = true;
    public bool MismatchTrackerTrackPbdFiles { get; set; } = true;

}
