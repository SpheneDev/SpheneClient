using Sphene.SpheneConfiguration.Models;
using Sphene.UI;
using Sphene.UI.Components;
using Microsoft.Extensions.Logging;
using Sphene.API.Data;
using Sphene.API.Dto.CharaData;

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
    // Enable or disable integration of ShrinkU UI inside Sphene
    public bool EnableShrinkUIntegration { get; set; } = true;
    
    // Acknowledgment System Settings
    public bool ShowAcknowledgmentPopups { get; set; } = false;
    public bool ShowWaitingForAcknowledgmentPopups { get; set; } = false;
    public bool EnableAcknowledgmentBatching { get; set; } = true;
    public bool EnableAcknowledgmentAutoRetry { get; set; } = true;

    public int AcknowledgmentTimeoutSeconds { get; set; } = 30;
    public NotificationLocation AcknowledgmentNotification { get; set; } = NotificationLocation.Toast;
    
    // Incognito Mode Settings
    public bool IsIncognitoModeActive { get; set; } = false;
    public HashSet<string> PrePausedPairs { get; set; } = new();
    public HashSet<string> PrePausedSyncshells { get; set; } = new();
    
    // Housing Ownership Settings
    public List<LocationInfo> OwnedHousingProperties { get; set; } = new();
    public List<VerifiedHousingProperty> VerifiedOwnedHousingProperties { get; set; } = new();
    
    // Theme Settings
    public string SelectedThemeName { get; set; } = "Default Sphene";
    public string SelectedTheme { get; set; } = "Default Sphene";
    public bool AutoLoadThemeOnStartup { get; set; } = true;

    // Release Changelog
    public string LastSeenVersionChangelog { get; set; } = string.Empty;
    public string ReleaseChangelogUrl { get; set; } = string.Empty;

    public bool UseTestServerOverride { get; set; } = false;
    public string TestServerApiUrl { get; set; } = string.Empty;

}