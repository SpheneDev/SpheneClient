using Sphene.Services.Mediator;
using Sphene.Services.ServerConfiguration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Sphene.Services;

public class CharacterIdentityService : MediatorSubscriberBase, IHostedService
{
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly DalamudUtilService _dalamudUtilService;
    private bool _hasCheckedThisSession;

    public CharacterIdentityService(ILogger<CharacterIdentityService> logger, SpheneMediator mediator,
        ServerConfigurationManager serverConfigurationManager, DalamudUtilService dalamudUtilService)
        : base(logger, mediator)
    {
        _serverConfigurationManager = serverConfigurationManager;
        _dalamudUtilService = dalamudUtilService;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Mediator.Subscribe<DalamudLoginMessage>(this, (msg) => { _ = CheckCharacterIdentityAsync(); });
        Mediator.Subscribe<DalamudLogoutMessage>(this, (msg) => _hasCheckedThisSession = false);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        UnsubscribeAll();
        return Task.CompletedTask;
    }

    private async Task CheckCharacterIdentityAsync()
    {
        if (_hasCheckedThisSession) return;

        await Task.Delay(2000).ConfigureAwait(false);

        try
        {
            var charaName = await _dalamudUtilService.GetPlayerNameAsync().ConfigureAwait(false);
            var worldId = await _dalamudUtilService.GetHomeWorldIdAsync().ConfigureAwait(false);
            var cid = await _dalamudUtilService.GetCIDAsync().ConfigureAwait(false);

            if (string.IsNullOrEmpty(charaName) || worldId == 0 || cid == 0)
            {
                Logger.LogDebug("Cannot check character identity: player data not available");
                return;
            }

            var server = _serverConfigurationManager.CurrentServer;
            var auth = server.Authentications.FirstOrDefault(a =>
                a.LastSeenCID != null && a.LastSeenCID != 0 && a.LastSeenCID == cid);

            if (auth == null)
            {
                Logger.LogDebug("No known CID {cid} in authentications — new character or not configured", cid);
                _hasCheckedThisSession = true;
                return;
            }

            if (!string.Equals(auth.CharacterName, charaName, StringComparison.Ordinal) || auth.WorldId != worldId)
            {
                Logger.LogInformation("Auto-updating character identity for CID {cid}: {oldName}@{oldWorld} -> {newName}@{newWorld}",
                    cid, auth.CharacterName, auth.WorldId, charaName, worldId);
                auth.CharacterName = charaName;
                auth.WorldId = worldId;
                _serverConfigurationManager.Save();

                Mediator.Publish(new RequestSpheneReconnectMessage());
                Logger.LogDebug("Published RequestSpheneReconnectMessage after identity update");
            }
            else
            {
                Logger.LogDebug("Character identity unchanged for CID {cid} ({chara}@{world})", cid, charaName, worldId);
            }

            _hasCheckedThisSession = true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error checking character identity");
        }
    }
}
