namespace Sphene.SpheneConfiguration.Models;

public class NotificationSoundConfig
{
    public bool Enabled { get; set; } = false;
    public SoundOutputMode OutputMode { get; set; } = SoundOutputMode.GameSystem;
    public string SelectedGameSoundName { get; set; } = "Default";
    public string CustomSoundPath { get; set; } = string.Empty;
    public float Volume { get; set; } = 0.3f;
}
