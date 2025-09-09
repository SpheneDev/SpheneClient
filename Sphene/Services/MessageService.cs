using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using Sphene.SpheneConfiguration.Models;
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
    private readonly ConcurrentDictionary<string, Notification> _messages = new();
    private readonly ConcurrentDictionary<string, List<Notification>> _taggedMessages = new();
    private readonly object _lock = new();

    public MessageService(ILogger<MessageService> logger, INotificationManager? notificationManager = null)
    {
        _logger = logger;
        _notificationManager = notificationManager;
    }

    public int Count => _messages.Count;
    public bool IsEmpty => _messages.IsEmpty;

    // Add a tagged message that can be cleaned up later
    public void AddTaggedMessage(string tag, string content, NotificationType type = NotificationType.Info, 
        string? title = null, TimeSpan? minimumDuration = null, bool respectUiHidden = true)
    {
        var notification = new Notification(content, type, title, minimumDuration, respectUiHidden, tag);
        var messageId = $"{tag}_{DateTime.UtcNow.Ticks}";
        
        lock (_lock)
        {
            _messages[messageId] = notification;
            
            if (!_taggedMessages.ContainsKey(tag))
                _taggedMessages[tag] = new List<Notification>();
            
            _taggedMessages[tag].Add(notification);
        }

        // Send to Dalamud notification system if available
        if (_notificationManager != null)
        {
            var dalNotification = notification.ToINotification() as Dalamud.Interface.ImGuiNotification.Notification;
            if (dalNotification != null)
                _notificationManager.AddNotification(dalNotification);
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
}