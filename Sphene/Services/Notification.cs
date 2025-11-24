using System;
using Dalamud.Interface.ImGuiNotification;
using NotificationType = Sphene.SpheneConfiguration.Models.NotificationType;

namespace Sphene.Services;

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
