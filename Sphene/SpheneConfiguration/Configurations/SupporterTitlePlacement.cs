namespace Sphene.SpheneConfiguration.Configurations;

/// <summary>
/// How the local player's game title is placed on their nameplate.
/// <see cref="GameDefault"/> leaves the game's own nameplate setting untouched.
/// </summary>
public enum SupporterTitlePlacement
{
    GameDefault = 0,
    AboveName = 1,
    BelowName = 2,
    Hidden = 3
}
