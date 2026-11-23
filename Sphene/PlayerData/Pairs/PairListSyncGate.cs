namespace Sphene.PlayerData.Pairs;

/// <summary>
/// Tracks whether the authoritative pair list was pulled since the last
/// clear. Server pushes that land before the first full sweep must not
/// satisfy this gate - they only patch individual rows. Only a completed
/// GroupsGetAll/UserGetPairedClients sweep marks it loaded; clearing the
/// local pairs re-arms it.
/// </summary>
internal sealed class PairListSyncGate
{
    /// <summary>True once a full pair-list sweep finished.</summary>
    public bool InitialListLoaded { get; private set; }

    /// <summary>True until the first full sweep completed - partial pushes
    /// must never count as "list present".</summary>
    public bool RequiresInitialLoad => !InitialListLoaded;

    public void MarkInitialListLoaded() => InitialListLoaded = true;

    public void OnPairsCleared() => InitialListLoaded = false;
}
