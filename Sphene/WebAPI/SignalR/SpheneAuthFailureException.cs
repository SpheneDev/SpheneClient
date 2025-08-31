namespace Sphene.WebAPI.SignalR;

public class SpheneAuthFailureException : Exception
{
    public SpheneAuthFailureException(string reason)
    {
        Reason = reason;
    }

    public string Reason { get; }
}
