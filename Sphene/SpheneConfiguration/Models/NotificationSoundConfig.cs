namespace Sphene.SpheneConfiguration.Models;

public class NotificationSoundConfig
{
    public bool Enabled { get; set; } = true;
    public SoundOutputMode OutputMode { get; set; } = SoundOutputMode.SpheneDefault;
    public string SelectedGameSoundName { get; set; } = "Default";
    public string SelectedSpheneDefaultSound { get; set; } = "Default";
    public string CustomSoundPath { get; set; } = string.Empty;
    public float Volume { get; set; } = 0.3f;
}
