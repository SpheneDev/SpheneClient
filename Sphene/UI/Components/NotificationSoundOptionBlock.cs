using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Sphene.Services;
using Sphene.SpheneConfiguration;
using Sphene.SpheneConfiguration.Models;

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
        if (ImGui.Checkbox("Enabled##" + blockId, ref enabled))
        {
            soundConfig.Enabled = enabled;
            changed = true;
        }

        var outputMode = (int)soundConfig.OutputMode;
        if (ImGui.Combo("Output Mode##" + blockId, ref outputMode, "Game System\0Sphene Default\0Custom Sound\0"))
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
                if (ImGui.Combo("Sound##" + blockId, ref currentIndex, soundNames))
                {
                    soundConfig.SelectedGameSoundName = registry[currentIndex].Name;
                    changed = true;
                }
            }
            else
            {
                UiSharedService.ColorTextWrapped("No predefined game sounds available.", ImGuiColors.DalamudGrey);
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
                if (ImGui.Combo("Built-in Sound##" + blockId, ref currentIndex, soundNames))
                {
                    soundConfig.SelectedSpheneDefaultSound = registry[currentIndex].Name;
                    changed = true;
                }
            }
            else
            {
                UiSharedService.ColorTextWrapped("No built-in sounds available.", ImGuiColors.DalamudGrey);
            }
        }
        else if (soundConfig.OutputMode == SoundOutputMode.CustomSound)
        {
            ImGui.Text("Sound File");
            var customPath = soundConfig.CustomSoundPath;
            var inputAvailWidth = ImGui.GetContentRegionAvail().X;
            var browseBtnWidth = ImGui.CalcTextSize("Browse...").X + ImGui.GetStyle().FramePadding.X * 2f;
            ImGui.SetNextItemWidth(inputAvailWidth - browseBtnWidth - ImGui.GetStyle().ItemSpacing.X);
            if (ImGui.InputText("##CustomSoundPath" + blockId, ref customPath, 256))
            {
                soundConfig.CustomSoundPath = customPath;
                changed = true;
            }

            if (fileDialogManager != null)
            {
                ImGui.SameLine();
                if (ImGui.Button("Browse...##" + blockId))
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
        }

        var volume = soundConfig.Volume;
        if (ImGui.SliderFloat("Volume##" + blockId, ref volume, 0.0f, 1.0f, "%.2f"))
        {
            soundConfig.Volume = volume;
            changed = true;
        }

        if (onTestSound != null && ImGui.Button("Test Sound##" + blockId))
        {
            onTestSound(soundConfig);
        }

        return changed;
    }
}
