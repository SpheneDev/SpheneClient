using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace Sphene.Services;

public interface IChatSender
{
    void SendMessage(string message);
    void SendCommand(string command);
}

public class ChatSender : IChatSender
{
    private readonly IChatGui _chatGui;
    private readonly ILogger<ChatSender> _logger;

    public ChatSender(IChatGui chatGui, ILogger<ChatSender> logger)
    {
        _chatGui = chatGui;
        _logger = logger;
    }

    public void SendMessage(string message)
    {
        try
        {
            var seString = new SeStringBuilder().AddText(message).BuiltString;
            _chatGui.Print(seString);
            _logger.LogDebug("Sent chat message: {Message}", message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send chat message: {Message}", message);
        }
    }

    public void SendCommand(string command)
    {
        try
        {
            // Ensure command starts with /
            if (!command.StartsWith("/"))
            {
                command = "/" + command;
            }
            
            // Use UIModule to actually execute the command
            unsafe
            {
                using var msg = new Utf8String(command);
                UIModule.Instance()->ProcessChatBoxEntry(&msg);
            }
            
            _logger.LogDebug("Executed chat command: {Command}", command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute chat command: {Command}", command);
        }
    }
}