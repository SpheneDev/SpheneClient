using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using Sphene.SpheneConfiguration;
using Sphene.SpheneConfiguration.Models;
using Sphene.Services.Mediator;
using Sphene.Services.Events;
using Sphene.API.Dto.Files;
using Sphene.Services.ServerConfiguration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NotificationType = Sphene.SpheneConfiguration.Models.NotificationType;

namespace Sphene.Services;

public class NotificationService : DisposableMediatorSubscriberBase, IHostedService
{
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly INotificationManager _notificationManager;
    private readonly IChatGui _chatGui;
    private readonly SpheneConfigService _configurationService;
    private readonly Sphene.PlayerData.Factories.FileDownloadManagerFactory _fileDownloadManagerFactory;
    private readonly ShrinkU.Services.TextureBackupService _shrinkuBackupService;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly Lock _penumbraModToastLock = new();
    private readonly HashSet<string> _pendingPenumbraModToastHashes = new(StringComparer.OrdinalIgnoreCase);
    private string? _pendingPenumbraModToastLastSender;
    private string? _pendingPenumbraModToastLastModName;
    private CancellationTokenSource? _penumbraModToastCts;

    public NotificationService(ILogger<NotificationService> logger, SpheneMediator mediator,
        DalamudUtilService dalamudUtilService,
        INotificationManager notificationManager,
        IChatGui chatGui, SpheneConfigService configurationService,
        Sphene.PlayerData.Factories.FileDownloadManagerFactory fileDownloadManagerFactory,
        ShrinkU.Services.TextureBackupService shrinkuBackupService,
        ServerConfigurationManager serverConfigurationManager) : base(logger, mediator)
    {
        _dalamudUtilService = dalamudUtilService;
        _notificationManager = notificationManager;
        _chatGui = chatGui;
        _configurationService = configurationService;
        _fileDownloadManagerFactory = fileDownloadManagerFactory;
        _shrinkuBackupService = shrinkuBackupService;
        _serverConfigurationManager = serverConfigurationManager;
        
        mediator.Subscribe<FileTransferNotificationMessage>(this, OnFileTransferNotification);
        mediator.Subscribe<InstallReceivedPenumbraModMessage>(this, msg => _ = HandlePenumbraModTransferAsync(msg.Notification));
    }

    private string GetSenderDisplayName(FileTransferNotificationDto notification)
    {
        var uid = notification.Sender?.UID ?? string.Empty;
        var aliasOrUid = notification.Sender?.AliasOrUID ?? string.Empty;
        var name = _serverConfigurationManager.GetPreferredUserDisplayName(uid, aliasOrUid);
        return string.IsNullOrWhiteSpace(name) ? "Unknown" : name;
    }

    private void OnFileTransferNotification(FileTransferNotificationMessage message)
    {
        var notification = message.Notification;
        if (!_configurationService.Current.AllowReceivingPenumbraMods &&
            (!string.IsNullOrWhiteSpace(notification.ModFolderName) || (notification.ModInfo != null && notification.ModInfo.Count > 0)))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(notification.ModFolderName))
        {
            if (notification.ModInfo != null && notification.ModInfo.Count > 0)
            {
                foreach (var mod in notification.ModInfo)
                {
                    var hash = (mod.Hash ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(hash))
                    {
                        continue;
                    }

                    var modFolderName = string.IsNullOrWhiteSpace(mod.Name) ? hash : mod.Name;
                    var splitNotification = new FileTransferNotificationDto
                    {
                        Sender = notification.Sender,
                        Recipient = notification.Recipient,
                        Hash = hash,
                        FileName = notification.FileName,
                        ModFolderName = modFolderName,
                        Description = notification.Description,
                        ModInfo = new List<ModInfoDto>(1) { mod }
                    };

                    _ = PreparePenumbraModPopupAsync(splitNotification);
                }

                return;
            }

            var title = "Received file from " + GetSenderDisplayName(notification);
            var content = "New file is available for download.";

            var toast = new Notification(
                content,
                NotificationType.Info,
                title,
                TimeSpan.FromSeconds(10),
                respectUiHidden: false,
                tag: "FileTransfer");

            var dalamudNotification = toast.ToINotification() as Dalamud.Interface.ImGuiNotification.Notification;
            if (dalamudNotification != null)
            {
                _notificationManager.AddNotification(dalamudNotification);
            }
        }
        else
        {
            _ = PreparePenumbraModPopupAsync(notification);
        }
    }

    private async Task PreparePenumbraModPopupAsync(FileTransferNotificationDto notification)
    {
        try
        {
            if (!_configurationService.Current.AllowReceivingPenumbraMods)
            {
                return;
            }

            if (!_configurationService.Current.EnableShrinkUIntegration)
            {
                return;
            }

            var hashString = notification.Hash ?? string.Empty;
            var parts = hashString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var hash = parts.Length > 0 ? parts[0] : string.Empty;
            if (string.IsNullOrWhiteSpace(hash))
            {
                return;
            }

            var timeoutAt = DateTime.UtcNow.AddMinutes(1);
            var isAvailable = false;
            while (DateTime.UtcNow < timeoutAt)
            {
                try
                {
                    using var manager = _fileDownloadManagerFactory.Create();
                    isAvailable = await manager.IsFileAvailableAsync(hash, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Error while checking Penumbra mod availability for {hash}", hash);
                }

                if (isAvailable)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }

            if (!isAvailable)
            {
                return;
            }

            Mediator.Publish(new PenumbraModTransferAvailableMessage(notification));

            EnqueuePenumbraModToast(notification);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error preparing Penumbra mod popup for {mod}", notification.ModFolderName);
        }
    }

    private void EnqueuePenumbraModToast(FileTransferNotificationDto notification)
    {
        var hashString = notification.Hash ?? string.Empty;
        var parts = hashString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hash = parts.Length > 0 ? parts[0] : string.Empty;
        if (string.IsNullOrWhiteSpace(hash))
        {
            return;
        }

        var sender = GetSenderDisplayName(notification);
        var modName = notification.ModFolderName ?? string.Empty;

        CancellationTokenSource cts;
        lock (_penumbraModToastLock)
        {
            _pendingPenumbraModToastHashes.Add(hash);
            _pendingPenumbraModToastLastSender = sender;
            _pendingPenumbraModToastLastModName = modName;

            if (_penumbraModToastCts != null)
            {
                try { _penumbraModToastCts.Cancel(); }
                catch (Exception ex) { Logger.LogDebug(ex, "Failed to cancel pending Penumbra mod toast"); }
                try { _penumbraModToastCts.Dispose(); }
                catch (Exception ex) { Logger.LogDebug(ex, "Failed to dispose pending Penumbra mod toast cancellation token source"); }
            }

            _penumbraModToastCts = new CancellationTokenSource();
            cts = _penumbraModToastCts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            int count;
            string? lastSender;
            string? lastModName;
            lock (_penumbraModToastLock)
            {
                if (!ReferenceEquals(cts, _penumbraModToastCts))
                {
                    return;
                }

                count = _pendingPenumbraModToastHashes.Count;
                lastSender = _pendingPenumbraModToastLastSender;
                lastModName = _pendingPenumbraModToastLastModName;

                _pendingPenumbraModToastHashes.Clear();
                _pendingPenumbraModToastLastSender = null;
                _pendingPenumbraModToastLastModName = null;

                try { _penumbraModToastCts.Dispose(); }
                catch (Exception ex) { Logger.LogDebug(ex, "Failed to dispose Penumbra mod toast cancellation token source"); }
                _penumbraModToastCts = null;
            }

            if (count <= 0)
            {
                return;
            }

            string title;
            string content;
            if (count == 1)
            {
                title = "Received file from " + (string.IsNullOrWhiteSpace(lastSender) ? "Unknown" : lastSender);
                content = $"New Penumbra mod package for '{(string.IsNullOrWhiteSpace(lastModName) ? "Unknown" : lastModName)}' is available.";
            }
            else
            {
                title = "New Penumbra mod packages available";
                content = $"{count} Penumbra mod packages are available for download.";
            }

            var toast = new Notification(
                content,
                NotificationType.Info,
                title,
                TimeSpan.FromSeconds(10),
                respectUiHidden: false,
                tag: "FileTransfer");

            var dalamudNotification = toast.ToINotification() as Dalamud.Interface.ImGuiNotification.Notification;
            if (dalamudNotification != null)
            {
                _notificationManager.AddNotification(dalamudNotification);
            }
        });
    }

    private async Task HandlePenumbraModTransferAsync(FileTransferNotificationDto notification)
    {
        var success = false;
        try
        {
            if (!_configurationService.Current.AllowReceivingPenumbraMods)
            {
                return;
            }
            if (!_configurationService.Current.EnableShrinkUIntegration)
            {
                return;
            }

            var hashString = notification.Hash ?? string.Empty;
            var parts = hashString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var hash = parts.Length > 0 ? parts[0] : string.Empty;
            if (string.IsNullOrWhiteSpace(hash))
            {
                return;
            }

            var modFolderName = notification.ModFolderName;
            if (string.IsNullOrWhiteSpace(modFolderName))
            {
                return;
            }

            Mediator.Publish(new PenumbraModTransferProgressMessage(notification,
                $"Preparing download for '{modFolderName}' from {GetSenderDisplayName(notification)}…",
                null));

            using var manager = _fileDownloadManagerFactory.Create();

            var downloadProgress = new Progress<(long TransferredBytes, long TotalBytes)>(tuple =>
            {
                var transferred = tuple.TransferredBytes;
                var total = tuple.TotalBytes;
                float? percent = null;
                if (total > 0)
                {
                    percent = (float)transferred / total * 0.5f;
                }

                string? overlay = null;
                if (total > 0)
                {
                    overlay = $"{Sphene.UI.UiSharedService.ByteToString(transferred, addSuffix: false)}/{Sphene.UI.UiSharedService.ByteToString(total)}";
                }

                var status = overlay == null
                    ? $"Downloading '{modFolderName}'…"
                    : $"Downloading '{modFolderName}'… ({overlay})";

                Mediator.Publish(new PenumbraModTransferProgressMessage(notification, status, percent));
            });

            // Determine download path
            string? destinationPath = null;
            var downloadFolder = _configurationService.Current.PenumbraModDownloadFolder;
            if (!string.IsNullOrWhiteSpace(downloadFolder))
            {
                try
                {
                    if (!Directory.Exists(downloadFolder))
                    {
                        Directory.CreateDirectory(downloadFolder);
                    }
                    
                    // Sanitize mod folder name for filename
                    var safeName = string.Join("_", modFolderName.Split(Path.GetInvalidFileNameChars()));
                    destinationPath = Path.Combine(downloadFolder, $"{safeName}_{hash}.pmp");
                }
                catch (Exception ex)
                {
                     Logger.LogWarning(ex, "Failed to use custom download folder {folder}, falling back to cache", downloadFolder);
                }
            }

            var pmpPath = await manager.DownloadPmpToCacheAsync(hash, CancellationToken.None, downloadProgress, destinationPath).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(pmpPath) || !File.Exists(pmpPath))
            {
                Mediator.Publish(new PenumbraModTransferProgressMessage(notification,
                    $"Failed to download mod package for '{modFolderName}'.",
                    null));
                return;
            }

            Mediator.Publish(new PenumbraModTransferProgressMessage(notification,
                $"Download completed. Installing '{modFolderName}'…",
                0.5f));

            var progress = new Progress<(string, int, int)>(tuple =>
            {
                var stepText = tuple.Item1 ?? string.Empty;
                var current = tuple.Item2;
                var total = tuple.Item3;
                float? percent = null;
                if (total > 0)
                {
                    percent = 0.5f + 0.5f * (float)current / total;
                }

                var status = string.IsNullOrWhiteSpace(stepText)
                    ? $"Installing '{modFolderName}'… ({current}/{total})"
                    : stepText;

                Mediator.Publish(new PenumbraModTransferProgressMessage(notification, status, percent));
            });

            var cleanup = _configurationService.Current.DeletePenumbraModAfterInstall;
            var ok = await _shrinkuBackupService.RestorePmpAsync(modFolderName, pmpPath, progress, CancellationToken.None, cleanupBackupsAfterRestore: cleanup, deregisterDuringRestore: true).ConfigureAwait(false);
            if (!ok)
            {
                Mediator.Publish(new PenumbraModTransferProgressMessage(notification,
                    $"Failed to install Penumbra mod '{modFolderName}' from package.",
                    null));
                return;
            }

            Mediator.Publish(new PenumbraModTransferProgressMessage(notification,
                $"Installed Penumbra mod '{modFolderName}' from received package.",
                1.0f));
            success = true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error handling Penumbra mod transfer for {mod}", notification.ModFolderName);
        }
        finally
        {
            if (success)
            {
                var senderUid = notification.Sender.UID ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(senderUid) && !string.IsNullOrEmpty(notification.Hash))
                {
                    Mediator.Publish(new FileTransferAckMessage(notification.Hash, senderUid));
                }
            }

            Mediator.Publish(new PenumbraModTransferCompletedMessage(notification, success));
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Mediator.Subscribe<NotificationMessage>(this, ShowNotification);
        Mediator.Subscribe<AreaBoundSyncshellNotificationMessage>(this, ShowAreaBoundSyncshellNotification);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CancellationTokenSource? cts = null;
            lock (_penumbraModToastLock)
            {
                _pendingPenumbraModToastHashes.Clear();
                _pendingPenumbraModToastLastSender = null;
                _pendingPenumbraModToastLastModName = null;
                cts = _penumbraModToastCts;
                _penumbraModToastCts = null;
            }

            if (cts != null)
            {
                try { cts.Cancel(); }
                catch (Exception ex) { Logger.LogDebug(ex, "Failed to cancel Penumbra mod toast cancellation token source"); }
                try { cts.Dispose(); }
                catch (Exception ex) { Logger.LogDebug(ex, "Failed to dispose Penumbra mod toast cancellation token source"); }
            }
        }

        base.Dispose(disposing);
    }

    private void PrintErrorChat(string? message)
    {
        SeStringBuilder se = new SeStringBuilder().AddText("[Sphene] Error: " + message);
        _chatGui.PrintError(se.BuiltString);
    }

    private void PrintInfoChat(string? message)
    {
        SeStringBuilder se = new SeStringBuilder().AddText("[Sphene] Info: ").AddItalics(message ?? string.Empty);
        _chatGui.Print(se.BuiltString);
    }

    private void PrintWarnChat(string? message)
    {
        SeStringBuilder se = new SeStringBuilder().AddText("[Sphene] ").AddUiForeground("Warning: " + (message ?? string.Empty), 31).AddUiForegroundOff();
        _chatGui.Print(se.BuiltString);
    }

    private void ShowChat(NotificationMessage msg)
    {

        
        switch (msg.Type)
        {
            case NotificationType.Info:
                PrintInfoChat(msg.Message);
                break;

            case NotificationType.Warning:
                PrintWarnChat(msg.Message);
                break;

            case NotificationType.Error:
                PrintErrorChat(msg.Message);
                break;

            case NotificationType.Success:
                PrintInfoChat(msg.Message);
                break;
        }
    }

    private void ShowNotification(NotificationMessage msg)
    {
        if (!_dalamudUtilService.IsLoggedIn) 
        {
            return;
        }

        switch (msg.Type)
        {
            case NotificationType.Info:
                ShowNotificationLocationBased(msg, _configurationService.Current.InfoNotification);
                break;

            case NotificationType.Warning:
                ShowNotificationLocationBased(msg, _configurationService.Current.WarningNotification);
                break;

            case NotificationType.Error:
                ShowNotificationLocationBased(msg, _configurationService.Current.ErrorNotification);
                break;

            case NotificationType.Success:
                ShowNotificationLocationBased(msg, _configurationService.Current.AcknowledgmentNotification);
                break;
        }
    }

    private void ShowNotificationLocationBased(NotificationMessage msg, NotificationLocation location)
    {
        switch (location)
        {
            case NotificationLocation.Toast:
                ShowToast(msg);
                break;

            case NotificationLocation.Chat:
                ShowChat(msg);
                break;

            case NotificationLocation.Both:
                ShowToast(msg);
                ShowChat(msg);
                break;

            case NotificationLocation.Nowhere:
                break;
        }
    }

    private void ShowToast(NotificationMessage msg)
    {
        Dalamud.Interface.ImGuiNotification.NotificationType dalamudType = msg.Type switch
        {
            NotificationType.Error => Dalamud.Interface.ImGuiNotification.NotificationType.Error,
            NotificationType.Warning => Dalamud.Interface.ImGuiNotification.NotificationType.Warning,
            NotificationType.Info => Dalamud.Interface.ImGuiNotification.NotificationType.Info,
            _ => Dalamud.Interface.ImGuiNotification.NotificationType.Info
        };

        _notificationManager.AddNotification(new Dalamud.Interface.ImGuiNotification.Notification()
        {
            Content = msg.Message ?? string.Empty,
            Type = dalamudType,
            Title = msg.Title,
            InitialDuration = msg.TimeShownOnScreen ?? TimeSpan.FromSeconds(3),
            RespectUiHidden = false
        });
    }

    private void ShowAreaBoundSyncshellNotification(AreaBoundSyncshellNotificationMessage msg)
    {
        Logger.LogDebug("ShowAreaBoundSyncshellNotification called: {Title} - {Message} (Location: {Location})", msg.Title, msg.Message, msg.Location);
        
        if (!_dalamudUtilService.IsLoggedIn) 
        {
            Logger.LogDebug("User not logged in, skipping area-bound notification");
            return;
        }

        Logger.LogDebug("Showing area-bound notification via ShowNotificationLocationBased");
        // Use the specific location setting for area-bound syncshell notifications
        ShowNotificationLocationBased(new NotificationMessage(msg.Title, msg.Message, NotificationType.Info), msg.Location);
    }
}
