namespace Sphene.Services;

/// <summary>
/// One animated nameplate effect. Registered in <see cref="NameplateFx.All"/>
/// and addressed by a stable string <see cref="Id"/> so supporter styles can
/// name effects once the server ships per-tier assignments.
/// </summary>
public interface INameplateFx
{
    /// <summary>Stable lowercase dot id, e.g. <c>"bat.orbit"</c>.</summary>
    string Id { get; }

    /// <summary>Human-readable name shown in the supporter effect picker.</summary>
    string DisplayName { get; }

    /// <summary>Draws one frame of the effect for one plate.</summary>
    void Draw(in NameplateFxContext ctx);
}
