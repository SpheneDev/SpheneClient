using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using Sphene.SpheneConfiguration.Models;
using Sphene.SpheneConfiguration;
using Sphene.Services.Mediator;
using System.Collections.Concurrent;
using System.Collections;
using NotificationType = Sphene.SpheneConfiguration.Models.NotificationType;

namespace Sphene.Services;

// Notification class for acknowledgment feedback
public class Notification
{
    public string Content { get; }
    public NotificationType Type { get; }
    public string? Title { get; }
    public TimeSpan? MinimumDuration { get; }
    public bool RespectUiHidden { get; }
    public DateTime CreatedAt { get; }
    public string? Tag { get; }

    public Notification(string content, NotificationType type = NotificationType.Info, string? title = null, 
        TimeSpan? minimumDuration = null, bool respectUiHidden = true, string? tag = null)
    {
        Content = content;
        Type = type;
        Title = title;
        MinimumDuration = minimumDuration;
        RespectUiHidden = respectUiHidden;
        CreatedAt = DateTime.UtcNow;
        Tag = tag;
    }

    public INotification ToINotification()
    {
        var dalType = Type == NotificationType.Info ? Dalamud.Interface.ImGuiNotification.NotificationType.Info :
                     Type == NotificationType.Warning ? Dalamud.Interface.ImGuiNotification.NotificationType.Warning :
                     Type == NotificationType.Error ? Dalamud.Interface.ImGuiNotification.NotificationType.Error :
                     Dalamud.Interface.ImGuiNotification.NotificationType.Success;
        
        var notification = new Dalamud.Interface.ImGuiNotification.Notification()
        {
            Content = Content,
            Type = dalType,
            Title = Title,
            InitialDuration = MinimumDuration ?? TimeSpan.FromSeconds(3),
            RespectUiHidden = RespectUiHidden
        };
        return notification;
    }
}

// Message service for managing acknowledgment notifications
public class MessageService : IEnumerable<KeyValuePair<string, Notification>>
{
    private readonly ILogger<MessageService> _logger;
    private readonly INotificationManager? _notificationManager;
    private readonly SpheneConfigService? _configService;
    private readonly SpheneMediator? _mediator;
    private readonly ConcurrentDictionary<string, Notification> _messages = new();
    private readonly ConcurrentDictionary<string, List<Notification>> _taggedMessages = new();
    private readonly object _lock = new();

    public MessageService(ILogger<MessageService> logger, INotificationManager? notificationManager = null, SpheneConfigService? configService = null, SpheneMediator? mediator = null)
    {
        _logger = logger;
        _notificationManager = notificationManager;
        _configService = configService;
        _mediator = mediator;
    }

    public int Count => _messages.Count;
    public bool IsEmpty => _messages.IsEmpty;

    // Add a tagged message that can be cleaned up later
    public void AddTaggedMessage(string tag, string content, NotificationType type = NotificationType.Info, 
        string? title = null, TimeSpan? minimumDuration = null, bool respectUiHidden = true)
    {
        // Check if this is a "waiting for acknowledgment" message and if those popups are disabled
        if (IsWaitingForAcknowledgmentMessage(tag) && !ShouldShowWaitingForAcknowledgmentNotification())
        {
            _logger.LogDebug("Skipping waiting for acknowledgment notification due to settings: {tag} - {content}", tag, content);
            return;
        }
        
        // Check if this is an acknowledgment-related message and if acknowledgment popups are disabled
        if (IsAcknowledgmentMessage(tag) && !ShouldShowAcknowledgmentNotification())
        {
            _logger.LogDebug("Skipping acknowledgment notification due to settings: {tag} - {content}", tag, content);
            return;
        }
        
        var notification = new Notification(content, type, title, minimumDuration, respectUiHidden, tag);
        var messageId = $"{tag}_{DateTime.UtcNow.Ticks}";
        
        lock (_lock)
        {
            _messages[messageId] = notification;
            
            if (!_taggedMessages.ContainsKey(tag))
                _taggedMessages[tag] = new List<Notification>();
            
            _taggedMessages[tag].Add(notification);
        }

        // Send notification based on settings
        if (ShouldShowNotificationInLocation(tag))
        {

            
            SendNotificationBasedOnSettings(notification, tag);
        }
        
        _logger.LogInformation("Added tagged message: {tag} - {content}", tag, content);
    }

    // Add a regular message
    public void AddMessage(string content, NotificationType type = NotificationType.Info, 
        string? title = null, TimeSpan? minimumDuration = null, bool respectUiHidden = true)
    {
        var notification = new Notification(content, type, title, minimumDuration, respectUiHidden);
        var messageId = $"msg_{DateTime.UtcNow.Ticks}";
        
        _messages[messageId] = notification;
        
        // Send to Dalamud notification system if available
        if (_notificationManager != null)
        {
            var dalNotification = notification.ToINotification() as Dalamud.Interface.ImGuiNotification.Notification;
            if (dalNotification != null)
                _notificationManager.AddNotification(dalNotification);
        }
        
        _logger.LogInformation("Added message: {content}", content);
    }

    // Clean up all messages with a specific tag
    public void CleanTaggedMessages(string tag)
    {
        lock (_lock)
        {
            if (_taggedMessages.TryGetValue(tag, out var notifications))
            {
                var toRemove = new List<string>();
                
                foreach (var kvp in _messages)
                {
                    if (kvp.Value.Tag == tag)
                    {
                        toRemove.Add(kvp.Key);
                    }
                }
                
                foreach (var key in toRemove)
                {
                    _messages.TryRemove(key, out _);
                }
                
                _taggedMessages.TryRemove(tag, out _);
                _logger.LogInformation("Cleaned {count} messages with tag: {tag}", toRemove.Count, tag);
            }
        }
    }

    // Clean up old messages (older than specified age)
    public void CleanOldMessages(TimeSpan maxAge)
    {
        var cutoffTime = DateTime.UtcNow.Subtract(maxAge);
        var toRemove = new List<string>();
        
        foreach (var kvp in _messages)
        {
            if (kvp.Value.CreatedAt < cutoffTime)
            {
                toRemove.Add(kvp.Key);
            }
        }
        
        foreach (var key in toRemove)
        {
            _messages.TryRemove(key, out _);
        }
        
        if (toRemove.Count > 0)
        {
            _logger.LogInformation("Cleaned {count} old messages", toRemove.Count);
        }
    }

    // Clear all messages
    public void Clear()
    {
        lock (_lock)
        {
            _messages.Clear();
            _taggedMessages.Clear();
        }
        _logger.LogInformation("Cleared all messages");
    }

    // Get messages by tag
    public IEnumerable<Notification> GetMessagesByTag(string tag)
    {
        lock (_lock)
        {
            return _taggedMessages.TryGetValue(tag, out var notifications) 
                ? notifications.ToList() 
                : Enumerable.Empty<Notification>();
        }
    }

    public IEnumerator<KeyValuePair<string, Notification>> GetEnumerator()
    {
        return _messages.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
    
    // Check if a message tag indicates it's acknowledgment-related
    private bool IsAcknowledgmentMessage(string tag)
    {
        return tag.StartsWith("ack_") || 
               tag.StartsWith("pair_clear_") || 
               tag.StartsWith("pair_force_clear_") || 
               tag.StartsWith("build_start_pending") ||
               tag.Contains("acknowledgment", StringComparison.OrdinalIgnoreCase);
    }
    
    // Check if a message tag indicates it's a "waiting for acknowledgment" message
    private bool IsWaitingForAcknowledgmentMessage(string tag)
    {
        return tag.StartsWith("ack_") && !tag.Contains("success") && !tag.Contains("complete");
    }
    
    // Check if acknowledgment notifications should be shown based on settings
    private bool ShouldShowAcknowledgmentNotification()
    {
        if (_configService?.Current == null)
            return true; // Default to showing if config is not available
            
        return _configService.Current.ShowAcknowledgmentPopups;
    }
    
    // Check if "waiting for acknowledgment" notifications should be shown based on settings
    private bool ShouldShowWaitingForAcknowledgmentNotification()
    {
        if (_configService?.Current == null)
            return false; // Default to not showing if config is not available
            
        return _configService.Current.ShowWaitingForAcknowledgmentPopups;
    }
    
    // Check if notification should be shown in the current location (Toast vs Chat)
    private bool ShouldShowNotificationInLocation(string tag)
    {
        if (!IsAcknowledgmentMessage(tag))
            return true; // Non-acknowledgment messages always show
            
        if (_configService?.Current == null)
            return true; // Default to showing if config is not available
            
        // Check specifically for "waiting for acknowledgment" messages
        if (IsWaitingForAcknowledgmentMessage(tag) && !_configService.Current.ShowWaitingForAcknowledgmentPopups)
            return false; // Don't show waiting for acknowledgment notifications if disabled
            
        if (!_configService.Current.ShowAcknowledgmentPopups)
            return false; // Don't show any acknowledgment notifications if disabled
            
        var location = _configService.Current.AcknowledgmentNotification;
        
        // Show notifications if location is not None
        return location != NotificationLocation.Nowhere;
    }
    
    // Send notification based on acknowledgment settings
    private void SendNotificationBasedOnSettings(Notification notification, string tag)
    {
        if (!IsAcknowledgmentMessage(tag))
        {
            // Non-acknowledgment messages always show as toast
            if (_notificationManager != null)
            {
                var dalNotification = notification.ToINotification() as Dalamud.Interface.ImGuiNotification.Notification;
                if (dalNotification != null)
                    _notificationManager.AddNotification(dalNotification);
            }
            return;
        }
        
        if (_configService?.Current == null)
        {
            // Default to toast if config is not available
            if (_notificationManager != null)
            {
                var dalNotification = notification.ToINotification() as Dalamud.Interface.ImGuiNotification.Notification;
                if (dalNotification != null)
                    _notificationManager.AddNotification(dalNotification);
            }
            return;
        }
        
        var location = _configService.Current.AcknowledgmentNotification;
        

            
        switch (location)
        {
            case NotificationLocation.Toast:
                if (_notificationManager != null)
                {
                    var dalNotification = notification.ToINotification() as Dalamud.Interface.ImGuiNotification.Notification;
                    if (dalNotification != null)
                        _notificationManager.AddNotification(dalNotification);
                }
                break;
                
            case NotificationLocation.Chat:
                if (_mediator != null)
                {
                    var notificationMessage = new NotificationMessage(notification.Title ?? "Sphene", notification.Content, notification.Type, notification.MinimumDuration);

                    _mediator.Publish(notificationMessage);
                }
                else
                {
                    _logger.LogWarning("Cannot send chat notification: SpheneMediator is null");
                }
                break;
                
            case NotificationLocation.Both:
                // Send both toast and chat
                if (_notificationManager != null)
                {
                    var dalNotification = notification.ToINotification() as Dalamud.Interface.ImGuiNotification.Notification;
                    if (dalNotification != null)
                        _notificationManager.AddNotification(dalNotification);
                }
                if (_mediator != null)
                {
                    var notificationMessage = new NotificationMessage(notification.Title ?? "Sphene", notification.Content, notification.Type, notification.MinimumDuration);

                    _mediator.Publish(notificationMessage);
                }
                else
                {
                    _logger.LogWarning("Cannot send chat notification (Both): SpheneMediator is null");
                }
                break;
                
            case NotificationLocation.Nowhere:
                // Don't send any notification
                break;
        }
    }
}