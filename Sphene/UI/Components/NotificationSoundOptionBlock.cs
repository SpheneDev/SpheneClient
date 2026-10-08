using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Sphene.Services;
using Sphene.SpheneConfiguration;
using Sphene.SpheneConfiguration.Models;
using Sphene.UI.Design;

namespace Sphene.UI.Components;

public static class NotificationSoundOptionBlock
{
    public static bool DrawNotificationSoundConfig(
        SpheneConfigService configService,
        UiSharedService uiShared,
        NotificationSoundConfig soundConfig,
        FileDialogManager? fileDialogManager = null,
        Action<NotificationSoundConfig>? onTestSound = null,
        string blockId = "")
    {
        var changed = false;

        var enabled = soundConfig.Enabled;
        if (SpheneUI.Controls.Toggle("Enabled##" + blockId, ref enabled))
        {
            soundConfig.Enabled = enabled;
            changed = true;
        }

        var outputMode = (int)soundConfig.OutputMode;
        if (SpheneUI.Controls.LabeledCombo("Output Mode##" + blockId, ref outputMode, "Game System\0Sphene Default\0Custom Sound\0"))
        {
            soundConfig.OutputMode = (SoundOutputMode)outputMode;
            changed = true;
        }

        if (soundConfig.OutputMode == SoundOutputMode.GameSystem)
        {
            var registry = GameSoundRegistry.Sounds;
            if (registry.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(soundConfig.SelectedGameSoundName))
                {
                    soundConfig.SelectedGameSoundName = registry[0].Name;
                    changed = true;
                }

                var soundNames = string.Join("\0", registry.Select(s => s.Name)) + "\0";
                var currentIndex = registry.Select((s, i) => new { s, i }).FirstOrDefault(x => string.Equals(x.s.Name, soundConfig.SelectedGameSoundName, StringComparison.Ordinal))?.i ?? 0;
                if (SpheneUI.Controls.LabeledCombo("Sound##" + blockId, ref currentIndex, soundNames))
                {
                    soundConfig.SelectedGameSoundName = registry[currentIndex].Name;
                    changed = true;
                }
            }
            else
            {
                SpheneUI.Text.ColoredWrapped("No predefined game sounds available.", ImGuiColors.DalamudGrey);
            }
        }
        else if (soundConfig.OutputMode == SoundOutputMode.SpheneDefault)
        {
            var registry = SpheneSoundRegistry.Sounds;
            if (registry.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(soundConfig.SelectedSpheneDefaultSound))
                {
                    soundConfig.SelectedSpheneDefaultSound = registry[0].Name;
                    changed = true;
                }

                var soundNames = string.Join("\0", registry.Select(s => s.Name)) + "\0";
                var currentIndex = registry.Select((s, i) => new { s, i }).FirstOrDefault(x => string.Equals(x.s.Name, soundConfig.SelectedSpheneDefaultSound, StringComparison.Ordinal))?.i ?? 0;
                if (SpheneUI.Controls.LabeledCombo("Built-in Sound##" + blockId, ref currentIndex, soundNames))
                {
                    soundConfig.SelectedSpheneDefaultSound = registry[currentIndex].Name;
                    changed = true;
                }
            }
            else
            {
                SpheneUI.Text.ColoredWrapped("No built-in sounds available.", ImGuiColors.DalamudGrey);
            }
        }
        else if (soundConfig.OutputMode == SoundOutputMode.CustomSound)
        {
            var customPath = soundConfig.CustomSoundPath;
            var caps = fileDialogManager == null
                ? Array.Empty<SpheneUI.Controls.InputButtonSpec>()
                : new[] { new SpheneUI.Controls.InputButtonSpec(Label: "Browse",
                    Tooltip: "Browse for a sound file.") };
            var (pathChanged, browsePressed) = SpheneUI.Controls.InputTextWithButtons(
                "Sound File##CustomSoundPath" + blockId, ref customPath, 256,
                buttons: caps);
            if (pathChanged)
            {
                soundConfig.CustomSoundPath = customPath;
                changed = true;
            }

            if (browsePressed == 0 && fileDialogManager != null)
            {
                fileDialogManager.OpenFileDialog("Select Sound File", ".wav,.mp3", (success, file) =>
                {
                    if (success && !string.IsNullOrWhiteSpace(file))
                    {
                        soundConfig.CustomSoundPath = file;
                        configService.Save();
                    }
                });
            }
        }

        var volume = soundConfig.Volume;
        if (SpheneUI.Controls.SliderFloat("Volume##" + blockId, ref volume, 0.0f, 1.0f, "%.2f"))
        {
            soundConfig.Volume = volume;
            changed = true;
        }

        if (onTestSound != null && SpheneUI.Controls.Button("Test Sound##" + blockId))
        {
            onTestSound(soundConfig);
        }

        return changed;
    }
}
