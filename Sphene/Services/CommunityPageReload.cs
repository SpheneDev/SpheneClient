using Sphene.API.Dto.Group;

namespace Sphene.Services;

/// <summary>Only the newest aggregate request may replace an open window's page.</summary>
internal sealed class CommunityPageReload
{
    private readonly Lock _gate = new();
    private object? _pending;
    private long _retryAt;

    public async Task Run(Func<Task<SyncshellCommunityDto?>> fetch, Action<SyncshellCommunityDto?> apply,
        bool refresh = true)
    {
        var request = new object();
        lock (_gate)
        {
            if (!refresh && (_pending != null || Environment.TickCount64 < _retryAt)) return;
            _pending = request;
        }
        try
        {
            var page = await fetch().ConfigureAwait(false);
            lock (_gate)
            {
                if (_pending == request) apply(page);
            }
        }
        finally
        {
            lock (_gate)
            {
                if (_pending == request)
                {
                    _pending = null;
                    _retryAt = Environment.TickCount64 + 5000;
                }
            }
        }
    }
}
