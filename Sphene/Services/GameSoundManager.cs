using System.IO;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Sound;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Sphene.SpheneConfiguration.Models;

namespace Sphene.Services;

public unsafe class GameSoundManager
{
    private readonly ILogger<GameSoundManager> _logger;

    public GameSoundManager(ILogger<GameSoundManager> logger)
    {
        _logger = logger;
    }

    public void PlayNotificationSound(NotificationSoundConfig config)
    {
        if (config == null || !config.Enabled)
        {
            return;
        }

        if (config.OutputMode == SoundOutputMode.CustomSound)
        {
            PlayCustomSound(config);
            return;
        }

        var sound = GameSoundRegistry.FindByName(config.SelectedGameSoundName);
        if (sound == null && !string.IsNullOrWhiteSpace(config.SelectedGameSoundName))
        {
            _logger.LogDebug("No predefined game sound found for name: {Name}", config.SelectedGameSoundName);
            return;
        }

        sound ??= GameSoundRegistry.Sounds.FirstOrDefault();
        if (sound == null)
        {
            _logger.LogDebug("No predefined game sounds available in registry");
            return;
        }

        var manager = SoundManager.Instance();
        if (manager == null)
        {
            _logger.LogDebug("SoundManager instance is null, skipping sound playback");
            return;
        }

        _logger.LogDebug("Playing system sound: {Path} (index {Index}, volume {Volume})", sound.Path, sound.Index, config.Volume);

        manager->PlaySystemSound(
            path: sound.Path,
            volume: config.Volume,
            soundNumber: sound.Index,
            fadeInDuration: 0,
            autoRelease: true,
            volumeCategory: SoundVolumeCategory.BypassVolumeRules);
    }

    private void PlayCustomSound(NotificationSoundConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.CustomSoundPath) || !File.Exists(config.CustomSoundPath))
        {
            _logger.LogDebug("Custom sound path is invalid or file does not exist: {Path}", config.CustomSoundPath);
            return;
        }

        var ext = Path.GetExtension(config.CustomSoundPath).ToLowerInvariant();

        try
        {
            if (string.Equals(ext, ".mp3", StringComparison.OrdinalIgnoreCase))
            {
                PlayMp3(config);
            }
            else if (string.Equals(ext, ".wav", StringComparison.OrdinalIgnoreCase))
            {
                PlayWav(config);
            }
            else
            {
                _logger.LogDebug("Unsupported custom sound file extension: {Extension}", ext);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to play custom sound: {Path}", config.CustomSoundPath);
        }
    }

    private static void PlayWav(NotificationSoundConfig config)
    {
        var wavData = File.ReadAllBytes(config.CustomSoundPath);

        if (config.Volume < 1.0f && config.Volume > 0.0f)
        {
            ApplyVolumeToWavData(wavData, config.Volume);
        }

        var ptr = Marshal.AllocHGlobal(wavData.Length);
        Marshal.Copy(wavData, 0, ptr, wavData.Length);

        NativeMethods.PlaySound(ptr, IntPtr.Zero, NativeMethods.SND_MEMORY | NativeMethods.SND_ASYNC | NativeMethods.SND_NODEFAULT);
    }

    private void PlayMp3(NotificationSoundConfig config)
    {
        _logger.LogDebug("Playing MP3 via NAudio: {Path} (volume {Volume})", config.CustomSoundPath, config.Volume);

        var reader = new Mp3FileReader(config.CustomSoundPath);
        var waveOut = new WaveOutEvent();

        var volumeProvider = new VolumeSampleProvider(reader.ToSampleProvider())
        {
            Volume = config.Volume
        };

        waveOut.Init(volumeProvider);

        waveOut.PlaybackStopped += (sender, args) =>
        {
            waveOut.Dispose();
            reader.Dispose();
        };

        waveOut.Play();
    }

    private static void ApplyVolumeToWavData(byte[] wavData, float volume)
    {
        var dataOffset = FindDataChunkOffset(wavData);
        if (dataOffset < 0)
        {
            return;
        }

        for (int i = dataOffset; i < wavData.Length - 1; i += 2)
        {
            short sample = (short)(wavData[i] | (wavData[i + 1] << 8));
            int scaled = (int)(sample * volume);
            if (scaled > short.MaxValue)
            {
                scaled = short.MaxValue;
            }
            if (scaled < short.MinValue)
            {
                scaled = short.MinValue;
            }

            short result = (short)scaled;
            wavData[i] = (byte)(result & 0xFF);
            wavData[i + 1] = (byte)((result >> 8) & 0xFF);
        }
    }

    private static int FindDataChunkOffset(byte[] wavData)
    {
        for (int i = 12; i < wavData.Length - 8; i++)
        {
            if (wavData[i] == 0x64 && wavData[i + 1] == 0x61 && wavData[i + 2] == 0x74 && wavData[i + 3] == 0x61)
            {
                return i + 8;
            }
        }

        return -1;
    }

    private static class NativeMethods
    {
        public const uint SND_ASYNC = 0x0001;
        public const uint SND_NODEFAULT = 0x0002;
        public const uint SND_MEMORY = 0x0004;

        [DllImport("winmm.dll", SetLastError = true)]
        public static extern bool PlaySound(IntPtr pszSound, IntPtr hmod, uint fdwSound);
    }
}
