namespace Sphene.UI.Syncshell;

/// <summary>Lifecycle slot for a managed event row - drives pill text/color,
/// the date-plate tint and text dimming. Selection is drawn independently.</summary>
internal enum PlannerEventState
{
    Upcoming,
    Live,
    Ended,
    Draft,
    InReview,
    Cancelled,
}
