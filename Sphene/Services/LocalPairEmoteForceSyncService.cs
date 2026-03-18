using Microsoft.Extensions.Logging;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Sphene.PlayerData.Pairs;
using Sphene.Services.Mediator;

namespace Sphene.Services;

public sealed class LocalPairEmoteForceSyncService : DisposableMediatorSubscriberBase
{
    public sealed record Result(
        bool Success,
        int AppliedCount,
        int SkippedNotVisibleCount,
        int SkippedMissingCount,
        string Message);

    private readonly ILogger<LocalPairEmoteForceSyncService> _logger;
    private readonly PairManager _pairManager;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly Lock _selectionLock = new();
    private readonly Lock _autoResetLock = new();
    private readonly Lock _leaderLock = new();
    private readonly HashSet<string> _selectedTargetUids = new(StringComparer.Ordinal);
    private static readonly TimeSpan AutoResetCooldown = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan LeaderFollowCooldown = TimeSpan.FromMilliseconds(300);
    private DateTime _lastAutoResetUtc = DateTime.MinValue;
    private DateTime _lastLeaderFollowUtc = DateTime.MinValue;
    private bool _autoResetOnLocalEmoteEnabled = true;
    private bool _followLeaderOnLeaderEmoteEnabled = true;
    private string? _leaderUid = null;

    public LocalPairEmoteForceSyncService(
        ILogger<LocalPairEmoteForceSyncService> logger,
        PairManager pairManager,
        DalamudUtilService dalamudUtilService,
        SpheneMediator mediator) : base(logger, mediator)
    {
        _logger = logger;
        _pairManager = pairManager;
        _dalamudUtilService = dalamudUtilService;
        Mediator.Subscribe<BypassEmoteMessage>(this, (msg) =>
        {
            if (!_autoResetOnLocalEmoteEnabled) return;
            if (string.IsNullOrWhiteSpace(msg.BypassEmoteData)) return;
            if (!TryBeginAutoReset()) return;
            _ = AutoResetSelectedTargetsOnLocalEmoteAsync();
        });
        Mediator.Subscribe<PairBypassEmoteReceivedMessage>(this, (msg) =>
        {
            if (!_followLeaderOnLeaderEmoteEnabled) return;
            if (!IsLeader(msg.Sender.UID)) return;
            if (!TryBeginLeaderFollow()) return;
            _ = FollowLeaderEmoteAsync();
        });
    }

    public bool AutoResetOnLocalEmoteEnabled => _autoResetOnLocalEmoteEnabled;
    public bool FollowLeaderOnLeaderEmoteEnabled => _followLeaderOnLeaderEmoteEnabled;

    public bool IsTargetSelected(string uid)
    {
        if (string.IsNullOrWhiteSpace(uid)) return false;
        lock (_selectionLock)
        {
            return _selectedTargetUids.Contains(uid);
        }
    }

    public bool ToggleTargetSelection(string uid)
    {
        if (string.IsNullOrWhiteSpace(uid)) return false;
        lock (_leaderLock)
        {
            lock (_selectionLock)
            {
                if (_selectedTargetUids.Contains(uid))
                {
                    _selectedTargetUids.Remove(uid);
                    return false;
                }

                _selectedTargetUids.Add(uid);
                if (string.Equals(_leaderUid, uid, StringComparison.Ordinal))
                {
                    _leaderUid = null;
                }
                return true;
            }
        }
    }

    public int GetSelectedTargetCount()
    {
        lock (_selectionLock)
        {
            return _selectedTargetUids.Count;
        }
    }

    public bool ToggleAutoResetOnLocalEmote()
    {
        _autoResetOnLocalEmoteEnabled = !_autoResetOnLocalEmoteEnabled;
        if (_autoResetOnLocalEmoteEnabled)
        {
            lock (_leaderLock)
            {
                _leaderUid = null;
            }
        }
        return _autoResetOnLocalEmoteEnabled;
    }

    public bool IsLeader(string uid)
    {
        if (string.IsNullOrWhiteSpace(uid)) return false;
        lock (_leaderLock)
        {
            return string.Equals(_leaderUid, uid, StringComparison.Ordinal);
        }
    }

    public bool ToggleLeader(string uid)
    {
        if (string.IsNullOrWhiteSpace(uid)) return false;
        lock (_leaderLock)
        {
            if (string.Equals(_leaderUid, uid, StringComparison.Ordinal))
            {
                _leaderUid = null;
                return false;
            }

            _leaderUid = uid;
            _autoResetOnLocalEmoteEnabled = false;
            lock (_selectionLock)
            {
                _selectedTargetUids.Remove(uid);
            }
            return true;
        }
    }

    public bool ToggleFollowLeaderOnLeaderEmote()
    {
        _followLeaderOnLeaderEmoteEnabled = !_followLeaderOnLeaderEmoteEnabled;
        return _followLeaderOnLeaderEmoteEnabled;
    }

    public async Task<Result> ForceSyncToSelectedVisibleTargetsAsync()
    {
        return await ResetSelectedVisibleTargetsAsync(includeLocalPlayer: true).ConfigureAwait(false);
    }

    private async Task AutoResetSelectedTargetsOnLocalEmoteAsync()
    {
        try
        {
            var result = await ResetSelectedVisibleTargetsAsync(includeLocalPlayer: false).ConfigureAwait(false);
            _logger.LogDebug(
                "Auto local timing sync result. success={success} applied={applied} skippedNotVisible={skippedNotVisible} skippedMissing={skippedMissing}",
                result.Success, result.AppliedCount, result.SkippedNotVisibleCount, result.SkippedMissingCount);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to auto reset selected target emote timing");
        }
    }

    private async Task FollowLeaderEmoteAsync()
    {
        try
        {
            var result = await ResetSelectedVisibleTargetsAsync(includeLocalPlayer: true, allowLocalOnlyWhenNoSelection: true).ConfigureAwait(false);
            _logger.LogDebug(
                "Leader-follow local timing sync result. success={success} applied={applied} skippedNotVisible={skippedNotVisible} skippedMissing={skippedMissing}",
                result.Success, result.AppliedCount, result.SkippedNotVisibleCount, result.SkippedMissingCount);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to follow leader emote timing reset");
        }
    }

    private bool TryBeginAutoReset()
    {
        lock (_autoResetLock)
        {
            var now = DateTime.UtcNow;
            if (now - _lastAutoResetUtc < AutoResetCooldown) return false;
            _lastAutoResetUtc = now;
            return true;
        }
    }

    private bool TryBeginLeaderFollow()
    {
        lock (_leaderLock)
        {
            var now = DateTime.UtcNow;
            if (now - _lastLeaderFollowUtc < LeaderFollowCooldown) return false;
            _lastLeaderFollowUtc = now;
            return true;
        }
    }

    private async Task<Result> ResetSelectedVisibleTargetsAsync(bool includeLocalPlayer, bool allowLocalOnlyWhenNoSelection = false)
    {
        List<string> selectedTargets;
        lock (_selectionLock)
        {
            selectedTargets = [.. _selectedTargetUids];
        }

        if (selectedTargets.Count == 0 && !(allowLocalOnlyWhenNoSelection && includeLocalPlayer))
        {
            return new Result(false, 0, 0, 0, "No targets selected.");
        }

        nint localPlayerAddress = nint.Zero;
        if (includeLocalPlayer)
        {
            localPlayerAddress = await _dalamudUtilService.GetPlayerPointerAsync().ConfigureAwait(false);
            if (localPlayerAddress == nint.Zero)
            {
                return new Result(false, 0, 0, 0, "Local player is not available.");
            }
        }

        var applied = 0;
        var skippedNotVisible = 0;
        var skippedMissing = 0;
        var staleSelections = new List<string>();
        var targetAddresses = new HashSet<nint>();

        foreach (var uid in selectedTargets)
        {
            var pair = _pairManager.GetPairByUID(uid);
            if (pair == null)
            {
                skippedMissing++;
                staleSelections.Add(uid);
                continue;
            }

            if (!pair.IsMutuallyVisible || !pair.IsVisible)
            {
                skippedNotVisible++;
                continue;
            }

            var address = pair.GetPlayerCharacterAddress();
            if (address == nint.Zero)
            {
                skippedNotVisible++;
                continue;
            }

            targetAddresses.Add(address);
        }

        if (includeLocalPlayer)
        {
            targetAddresses.Add(localPlayerAddress);
        }
        applied = await _dalamudUtilService.RunOnFrameworkThread(() => ResetAnimationsToZero(targetAddresses)).ConfigureAwait(false);

        if (staleSelections.Count > 0)
        {
            lock (_selectionLock)
            {
                foreach (var uid in staleSelections)
                {
                    _selectedTargetUids.Remove(uid);
                }
            }
        }

        _logger.LogDebug(
            "Local pair emote force sync completed. selected={selected} applied={applied} skippedNotVisible={skippedNotVisible} skippedMissing={skippedMissing}",
            selectedTargets.Count, applied, skippedNotVisible, skippedMissing);

        if (applied <= 0)
        {
            return new Result(
                false,
                applied,
                skippedNotVisible,
                skippedMissing,
                "No selected targets had an active resettable animation.");
        }

        return new Result(
            true,
            applied,
            skippedNotVisible,
            skippedMissing,
            $"Reset animation timing to 0 for {applied} character(s).");
    }

    private static int ResetAnimationsToZero(IEnumerable<nint> addresses)
    {
        var count = 0;
        foreach (var address in addresses)
        {
            if (TryResetAnimationToZero(address))
            {
                count++;
            }
        }

        return count;
    }

    private static unsafe bool TryResetAnimationToZero(nint characterAddress)
    {
        if (characterAddress == nint.Zero) return false;

        var character = (Character*)characterAddress;
        if (character->DrawObject == null) return false;
        if (character->DrawObject->GetObjectType() != ObjectType.CharacterBase) return false;
        if (((CharacterBase*)character->DrawObject)->GetModelType() != CharacterBase.ModelType.Human) return false;

        var human = (Human*)character->DrawObject;
        var skeleton = human->Skeleton;
        if (skeleton == null || skeleton->PartialSkeletonCount <= 0) return false;

        var partialSkeleton = &skeleton->PartialSkeletons[0];
        var animatedSkeleton = partialSkeleton->GetHavokAnimatedSkeleton(0);
        if (animatedSkeleton == null || animatedSkeleton->AnimationControls.Length <= 0) return false;

        var control = animatedSkeleton->AnimationControls[0].Value;
        if (control == null) return false;

        control->hkaAnimationControl.LocalTime = 0f;
        return true;
    }
}
