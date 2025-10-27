using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sphene.Services.Mediator;
using Sphene.SpheneConfiguration.Models;
using System.Timers;

namespace Sphene.Services;

public class HalloweenEasterEggService : DisposableMediatorSubscriberBase, IHostedService
{
    private readonly System.Timers.Timer _dailyCheckTimer;
    private bool _hasShownHalloweenNotification = false;

    public HalloweenEasterEggService(ILogger<HalloweenEasterEggService> logger, SpheneMediator mediator) 
        : base(logger, mediator)
    {
        // Check every hour for Halloween
        _dailyCheckTimer = new System.Timers.Timer(TimeSpan.FromHours(1).TotalMilliseconds);
        _dailyCheckTimer.Elapsed += OnTimerElapsed;
        _dailyCheckTimer.AutoReset = true;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Logger.LogDebug("Starting Halloween Easter Egg Service");
        _dailyCheckTimer.Start();
        
        // Check immediately on startup
        CheckForHalloween();
        
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Logger.LogDebug("Stopping Halloween Easter Egg Service");
        _dailyCheckTimer.Stop();
        return Task.CompletedTask;
    }

    private void OnTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        CheckForHalloween();
    }

    private void CheckForHalloween()
    {
        var now = DateTime.Now;
        
        // Check if it's Halloween (October 31st) or Halloween season (October 15-31)
        bool isHalloweenSeason = now.Month == 10 && now.Day >= 15 && now.Day <= 31;
        bool isHalloween = now.Month == 10 && now.Day == 31;
        
        if (isHalloweenSeason && !_hasShownHalloweenNotification)
        {
            ShowHalloweenNotification(isHalloween);
            _hasShownHalloweenNotification = true;
        }
        else if (!isHalloweenSeason)
        {
            // Reset the flag when Halloween season is over
            _hasShownHalloweenNotification = false;
        }
    }

    private void ShowHalloweenNotification(bool isActualHalloween)
    {
        string title = isActualHalloween ? " Happy Halloween! " : " Halloween is Coming! ";
        
        string[] spookyMessages = isActualHalloween 
            ? [
                "The veil between worlds grows thin... Perfect for character synchronization!",
                "Even ghosts need their glamours synchronized! Boo-tiful work, Warrior of Light!",
                "Your character data is so good, it's scary! Happy Halloween!",
                "May your mods be spooky and your synchronization be seamless!"
            ]
            : [
                "Halloween approaches... Time to prepare your spookiest glamours!",
                "The spirits whisper of upcoming costume parties... Are your mods ready?",
                "Something wicked this way comes... Better sync your character data!",
                "Halloween is near! Time to dust off those spooky outfits!"
            ];

        var random = new Random();
        string message = spookyMessages[random.Next(spookyMessages.Length)];
        
        Logger.LogDebug("Showing Halloween notification: {title} - {message}", title, message);
        
        Mediator.Publish(new NotificationMessage(
            title,
            message,
            NotificationType.Info,
            TimeSpan.FromSeconds(15)
        ));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dailyCheckTimer?.Stop();
            _dailyCheckTimer?.Dispose();
        }
        base.Dispose(disposing);
    }
}