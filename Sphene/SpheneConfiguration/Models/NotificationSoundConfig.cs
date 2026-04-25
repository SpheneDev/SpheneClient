namespace Sphene.SpheneConfiguration.Models;

public enum SoundOutputMode
{
    GameSystem,
    CustomSound
}

public class NotificationSoundConfig
{
    public bool Enabled { get; set; } = false;
    public SoundOutputMode OutputMode { get; set; } = SoundOutputMode.GameSystem;
    public string SelectedGameSoundName { get; set; } = "Default";
    public string CustomSoundPath { get; set; } = string.Empty;
    public float Volume { get; set; } = 1.0f;
}
