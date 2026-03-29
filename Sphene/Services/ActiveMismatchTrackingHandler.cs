using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sphene.PlayerData.Pairs;
using Sphene.Services.Mediator;
using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.Interop.Ipc;
using Sphene.PlayerData.Factories;

namespace Sphene.Services;

public sealed class ActiveMismatchTrackingHandler : IMediatorSubscriber, IHostedService, IDisposable
{
    private readonly ILogger<ActiveMismatchTrackingHandler> _logger;
    private readonly ActiveMismatchTrackerService _tracker;
    private readonly PairManager _pairManager;
    private readonly IpcManager _ipcManager;
    private readonly GameObjectHandlerFactory _gameObjectHandlerFactory;
    private DateTimeOffset _lastRefreshTime;
    private const int RefreshIntervalSeconds = 10;
    
    // Critical files that require redraw when mismatched
    private static readonly HashSet<string> CriticalBoneDeformerFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "chara/xls/bonedeformer/human.pbd",
        "chara/xls/bonedeformer/human.pdb" // common typo variant
    };
    
    public SpheneMediator Mediator { get; }

    public ActiveMismatchTrackingHandler(
        ILogger<ActiveMismatchTrackingHandler> logger,
        ActiveMismatchTrackerService tracker,
        PairManager pairManager,
        IpcManager ipcManager,
        GameObjectHandlerFactory gameObjectHandlerFactory,
        SpheneMediator mediator)
    {
        _logger = logger;
        _tracker = tracker;
        _pairManager = pairManager;
        _ipcManager = ipcManager;
        _gameObjectHandlerFactory = gameObjectHandlerFactory;
        Mediator = mediator;
        _lastRefreshTime = DateTimeOffset.UtcNow;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[MismatchTracker] Starting, subscribing to FrameworkUpdateMessage");
        Mediator.Subscribe<FrameworkUpdateMessage>(this, OnFrameworkUpdate);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[MismatchTracker] Stopping");
        Mediator.UnsubscribeAll(this);
        return Task.CompletedTask;
    }

    private void OnFrameworkUpdate(FrameworkUpdateMessage msg)
    {
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastRefreshTime).TotalSeconds < RefreshIntervalSeconds) return;

        _lastRefreshTime = now;

        try
        {
            var pairsToCheck = new HashSet<Pair>(_pairManager.DirectPairs);
            foreach (var groupPairs in _pairManager.GroupPairs.Values)
            {
                foreach (var pair in groupPairs)
                {
                    pairsToCheck.Add(pair);
                }
            }

            var visiblePairs = pairsToCheck.Where(p => p.IsVisible).ToList();
            _logger.LogDebug("[MismatchTracker] Checking {visible}/{total} visible pairs", visiblePairs.Count, pairsToCheck.Count);

            foreach (var pair in visiblePairs)
            {
                CheckPairForMismatches(pair);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh mismatch tracker");
        }
    }

    private void CheckPairForMismatches(Pair pair)
    {
        var characterData = pair.LastReceivedCharacterData;
        if (characterData == null)
        {
            _logger.LogDebug("[MismatchTracker] No character data for {uid}", pair.UserData.UID);
            return;
        }

        var uid = pair.UserData.UID;

        try
        {
            // Count this as ONE scan/check for this pair (not per-path)
            var deliveredByPath = BuildDeliveredPathState(characterData);
            
            // Get active paths for Player
            var playerActivePaths = pair.GetCurrentPenumbraActivePathsByGamePathAsync().GetAwaiter().GetResult();
            
            // Get active paths for Minion/Mount
            var minionActivePaths = pair.GetMinionOrMountActivePathsByGamePathAsync().GetAwaiter().GetResult();
            
            // Get active paths for Pet
            var petActivePaths = pair.GetPetActivePathsByGamePathAsync().GetAwaiter().GetResult();

            var activeDeliveredCount = deliveredByPath.Count(kvp => kvp.Value.IsActive);
            var mismatchCount = 0;
            var hasCriticalBoneDeformerMismatch = false;
            var hasAnyMismatch = false;

            foreach (var kvp in deliveredByPath)
            {
                var gamePath = kvp.Key;
                var state = kvp.Value;

                if (!state.IsActive) continue;

                // Determine which active paths to check based on ObjectKind
                var isMinionOrMountPath = state.ObjectKinds.Contains("MinionOrMount");
                var isPetPath = state.ObjectKinds.Contains("Pet");
                var activePaths = isMinionOrMountPath ? minionActivePaths 
                    : isPetPath ? petActivePaths 
                    : playerActivePaths;
                
                var isPenumbraActive = activePaths.TryGetValue(gamePath, out var activeSource) && !string.IsNullOrEmpty(activeSource);
                var isMismatch = !isPenumbraActive;
                
                if (isMismatch)
                {
                    mismatchCount++;
                    hasAnyMismatch = true;
                    
                    // Check if this is a critical bone deformer file
                    if (CriticalBoneDeformerFiles.Contains(gamePath) && !isMinionOrMountPath && !isPetPath)
                    {
                        hasCriticalBoneDeformerMismatch = true;
                        _logger.LogWarning("[MismatchTracker] Critical bone deformer mismatch detected for {uid}: {path}", uid, gamePath);
                    }
                }
                
                _tracker.RecordCheck(uid, gamePath, isMismatch, state.Sources, state.ObjectKinds);
            }

            // Record one scan for this pair with mismatch status
            _tracker.RecordScan(uid, hasAnyMismatch);

            if (activeDeliveredCount > 0)
            {
                _logger.LogDebug("[MismatchTracker] {uid}: {active} active delivered, {mismatch} mismatches found", uid, activeDeliveredCount, mismatchCount);
            }
            
            // Trigger redraw if critical bone deformer mismatch detected
            if (hasCriticalBoneDeformerMismatch)
            {
                _logger.LogInformation("[MismatchTracker] Triggering redraw for {uid} due to bone deformer mismatch", uid);
                _ = TriggerRedrawForPairAsync(pair);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to track active mismatches for {uid}", uid);
        }
    }

    private async Task TriggerRedrawForPairAsync(Pair pair)
    {
        try
        {
            var playerAddress = pair.GetPlayerCharacterAddress();
            if (playerAddress == nint.Zero)
            {
                _logger.LogDebug("[MismatchTracker] Cannot redraw {uid} - player address is zero", pair.UserData.UID);
                return;
            }

            // Create a GameObjectHandler for the redraw
            using var handler = await _gameObjectHandlerFactory.Create(
                ObjectKind.Player, 
                () => playerAddress, 
                isWatched: false).ConfigureAwait(false);
            
            if (handler.Address == nint.Zero)
            {
                _logger.LogDebug("[MismatchTracker] Cannot redraw {uid} - handler address is zero", pair.UserData.UID);
                return;
            }

            // Use Penumbra Redraw API
            await _ipcManager.Penumbra.RedrawAsync(_logger, handler, Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("[MismatchTracker] Redraw triggered for {uid}", pair.UserData.UID);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MismatchTracker] Failed to trigger redraw for {uid}", pair.UserData.UID);
        }
    }

    private static Dictionary<string, DeliveredPathState> BuildDeliveredPathState(CharacterData characterData)
    {
        var result = new Dictionary<string, DeliveredPathState>(StringComparer.OrdinalIgnoreCase);

        if (characterData.FileReplacements == null) return result;

        foreach (var objectKvp in characterData.FileReplacements)
        {
            var objectKind = objectKvp.Key.ToString();
            foreach (var fileReplacement in objectKvp.Value)
            {
                if (fileReplacement.GamePaths == null) continue;
                foreach (var gamePath in fileReplacement.GamePaths)
                {
                    if (string.IsNullOrEmpty(gamePath)) continue;
                    var normalizedPath = gamePath.Replace('\\', '/').ToLowerInvariant();

                    if (!result.TryGetValue(normalizedPath, out var state))
                    {
                        state = new DeliveredPathState();
                        result[normalizedPath] = state;
                    }

                    state.IsActive = fileReplacement.IsActive;
                    state.Sources.Add(fileReplacement.Hash ?? "unknown");
                    state.ObjectKinds.Add(objectKind);
                }
            }
        }

        return result;
    }

    public void Dispose()
    {
        Mediator.UnsubscribeAll(this);
    }

    private sealed class DeliveredPathState
    {
        public bool IsActive { get; set; }
        public HashSet<string> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ObjectKinds { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
