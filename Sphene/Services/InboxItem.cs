using Sphene.API.Data;

namespace Sphene.Services;

/// <summary>
/// One actionable inbox entry. <see cref="Payload"/> carries whatever the
/// row action needs  ·  a <c>SameThreadMessage</c> to republish (area-bound
/// consent) or a DTO reference; simple kinds resolve their target window
/// from <see cref="InboxKind"/> alone.
/// </summary>
public sealed record InboxItem(
    string Id,
    InboxKind Kind,
    string Title,
    string? Detail,
    DateTimeOffset Timestamp,
    object? Payload = null,
    UserData? Actor = null, string? Source = null, string? GroupGid = null);
