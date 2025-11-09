using System.Threading;

namespace Sphene.Services;

public class VisibilityGateService
{
    private int _gateActive = 0;

    public bool IsGateActive => Interlocked.CompareExchange(ref _gateActive, 0, 0) == 1;

    public void Activate()
    {
        Interlocked.Exchange(ref _gateActive, 1);
    }

    public void Deactivate()
    {
        Interlocked.Exchange(ref _gateActive, 0);
    }
}