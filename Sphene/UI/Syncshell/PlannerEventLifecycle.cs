using Sphene.API.Data.Enum;
using Sphene.API.Dto.Group;

namespace Sphene.UI.Syncshell;

/// <summary>Pure lifecycle classification for planner rows. Uses the same
/// effective-end rule as the list filter and the community view -
/// <c>EndUtc ?? StartUtc + 4h</c> - so an event without an end is "Live" for
/// four hours after its start and then "Ended"; no invented durations.</summary>
internal static class PlannerEventLifecycle
{
    public static PlannerEventState Classify(SyncshellEventDto ev, DateTime nowUtc)
    {
        if (ev.EventStatus == (int)EventLifecycleStatus.Cancelled)
        {
            return PlannerEventState.Cancelled;
        }
        if (ev.PublishStatus == (int)ContentPublishStatus.Draft)
        {
            return PlannerEventState.Draft;
        }
        if (ev.PublishStatus == (int)ContentPublishStatus.PendingApproval)
        {
            return PlannerEventState.InReview;
        }
        var effectiveEnd = ev.EndUtc ?? ev.StartUtc.AddHours(4);
        if (ev.EventStatus == (int)EventLifecycleStatus.Completed || effectiveEnd <= nowUtc)
        {
            return PlannerEventState.Ended;
        }
        return ev.StartUtc <= nowUtc ? PlannerEventState.Live : PlannerEventState.Upcoming;
    }
}
