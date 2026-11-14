using Sphene.API.Data.Enum;
using Sphene.API.Dto.User;

namespace Sphene.UI.Chronicles;

/// <summary>
/// Draft bag for the dedicated Chronicle editor - title, body, cover hash,
/// publishing options and the post being edited. Lives on the hub page so the
/// Live Preview window can bind the same instance: edits mirror immediately
/// and nothing is written to the server until Save runs.
/// </summary>
public sealed class ChronicleComposer
{
    /// <summary>The editor mode is visible - flips the Chronicles tab from
    /// list/detail into the authoring workspace.</summary>
    public bool Open { get; private set; }

    /// <summary>Id of the post being edited; 0 for a new Chronicle.</summary>
    public long EditId { get; private set; }

    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    /// <summary>Community-media hash of the cover image; null = none.</summary>
    public string? CoverMediaHash { get; set; }
    public bool Pinned { get; set; }
    public bool Draft { get; set; }
    public int Scope { get; set; } = (int)BlogVisibilityScope.Public;
    public DateTime? CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }
    public int Status { get; private set; } = (int)ContentPublishStatus.Draft;

    /// <summary>Baseline used to detect unsaved changes for Discard.</summary>
    private string _baselineFingerprint = string.Empty;

    public bool IsEditing => EditId != 0;

    /// <summary>Any field diverging from the seeded post/new defaults.</summary>
    public bool Dirty => !string.Equals(Fingerprint(), _baselineFingerprint,
        StringComparison.Ordinal);

    public void OpenNew()
    {
        Open = true;
        EditId = 0;
        Title = string.Empty;
        Body = string.Empty;
        CoverMediaHash = null;
        Pinned = false;
        Draft = true;
        Scope = (int)BlogVisibilityScope.Public;
        CreatedAt = null;
        UpdatedAt = null;
        Status = (int)ContentPublishStatus.Draft;
        _baselineFingerprint = Fingerprint();
    }

    public void OpenEdit(UserProfileBlogPostDto post)
    {
        Open = true;
        EditId = post.Id;
        Title = post.Title;
        Body = post.Body ?? string.Empty;
        CoverMediaHash = post.CoverMediaHash;
        Pinned = post.Pinned;
        Status = post.Status;
        Draft = post.Status == (int)ContentPublishStatus.Draft;
        Scope = post.VisibilityScope;
        CreatedAt = post.CreatedAt;
        UpdatedAt = post.UpdatedAt;
        _baselineFingerprint = Fingerprint();
    }

    public void Close() => Open = false;

    /// <summary>Replaces the draft with the saved server projection.</summary>
    public void CommitSaved(UserProfileBlogPostDto saved)
    {
        OpenEdit(saved);
        Close();
    }

    /// <summary>Draft DTO for the Live Preview - overlays unsaved fields onto
    /// the persisted shape; server-owned values keep the seeded state.</summary>
    public UserProfileBlogPostDto BuildPreviewDto() => new(EditId, Title,
        Body, Pinned, CreatedAt ?? DateTime.UtcNow, DateTime.UtcNow,
        Draft ? (int)ContentPublishStatus.Draft : (int)ContentPublishStatus.Published,
        Scope, null, null, 0, false, CoverMediaHash);

    private string Fingerprint()
        => string.Join('§', Title, Body, CoverMediaHash ?? string.Empty,
            Pinned.ToString(), Draft.ToString(), Scope.ToString());
}
