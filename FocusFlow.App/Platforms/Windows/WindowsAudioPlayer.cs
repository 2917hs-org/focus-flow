using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using FocusFlow.Application.Interfaces;
using FocusFlow.Domain.Models;

namespace FocusFlow.App.Platforms.Windows;

/// <summary>
/// FR-009/FR-010 on Windows, via MCI (winmm).
/// </summary>
/// <remarks>
/// <para>
/// MCI is used rather than PlaySound because the requirement asks for MP3 and volume
/// control, and PlaySound supports neither — it handles WAV and system aliases only, at
/// the system volume. MCI's mpegvideo device plays MP3 and its "setaudio ... volume to"
/// command gives 0-1000 gain, all from winmm which is already part of Windows. That
/// avoids taking a dependency on NAudio/LibVLC purely for these two requirements.
/// </para>
/// <para>
/// PlaySound is still used for the built-in system aliases, which are not files and so
/// cannot be opened by MCI. Those play at system volume — the volume slider does not
/// apply to them, only to file-based sounds.
/// </para>
/// </remarks>
public sealed class WindowsAudioPlayer : IAudioPlayer, IDisposable
{
    private const uint SndAsync = 0x0001;
    private const uint SndAlias = 0x00010000;
    private const uint SndNoDefault = 0x0002;

    private const string DefaultAlias = "SystemAsterisk";
    private const string Alias = "focusflow_audio";
    private const string AmbientAlias = "focusflow_ambient";

    private static readonly string[] Extensions = [".wav", ".mp3"];

    /// <summary>
    /// Built-in Windows sound-scheme aliases. These follow whatever the user has assigned
    /// in the Sound control panel, so they stay correct across themes.
    /// </summary>
    private static readonly AlarmSound[] Sounds =
    [
        new("Default (Asterisk)", null),
        new("Exclamation", "SystemExclamation"),
        new("Critical Stop", "SystemHand"),
        new("Question", "SystemQuestion"),
        new("Notification", "Notification.Default")
    ];

    /// <summary>Where FocusFlow.App.csproj copies Assets/Ambient/** at publish time — see
    /// its comment for why these ship as loose files instead of AvaloniaResource.</summary>
    private static readonly string AmbientSoundsDirectory =
        Path.Combine(AppContext.BaseDirectory, "Assets", "Ambient");

    private readonly Lock _gate = new();
    private readonly Lock _ambientGate = new();
    private readonly Lazy<IReadOnlyList<AlarmSound>> _ambientSounds = new(DiscoverAmbientSounds);
    private bool _mciOpen;
    private bool _ambientMciOpen;

    public IReadOnlyList<AlarmSound> AvailableSounds => Sounds;

    public IReadOnlyList<AlarmSound> AvailableAmbientSounds => _ambientSounds.Value;

    public IReadOnlyList<string> SupportedExtensions => Extensions;

    public void Play(string? sound, int volume)
    {
        var level = Math.Clamp(volume, TimerConfig.MinVolume, TimerConfig.MaxVolume);

        if (!string.IsNullOrWhiteSpace(sound) && File.Exists(sound) && TryPlayFile(sound, level))
        {
            return;
        }

        // Not a file (or MCI could not open it) — treat it as a system alias.
        var alias = string.IsNullOrWhiteSpace(sound) ? DefaultAlias : sound;
        if (!PlaySound(alias, IntPtr.Zero, SndAlias | SndAsync | SndNoDefault))
        {
            // Unknown alias from a stale config — fall back so the alarm is never silent.
            PlaySound(DefaultAlias, IntPtr.Zero, SndAlias | SndAsync | SndNoDefault);
        }
    }

    private bool TryPlayFile(string path, int volume)
    {
        lock (_gate)
        {
            CloseMci();

            // waveaudio and mpegvideo are the two MCI device types that cover the
            // required formats; letting MCI infer from the extension is less reliable
            // when the file association has been changed.
            var deviceType = Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)
                ? "waveaudio"
                : "mpegvideo";

            if (Send($"open \"{path}\" type {deviceType} alias {Alias}") != 0)
            {
                return false;
            }

            _mciOpen = true;

            // MCI volume is 0-1000; the slider is 0-100.
            Send(string.Format(CultureInfo.InvariantCulture, "setaudio {0} volume to {1}", Alias, volume * 10));

            if (Send($"play {Alias}") != 0)
            {
                CloseMci();
                return false;
            }

            return true;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            CloseMci();
        }
    }

    /// <summary>Must be called under <see cref="_gate"/>.</summary>
    private void CloseMci()
    {
        if (!_mciOpen)
        {
            return;
        }

        Send($"stop {Alias}");
        Send($"close {Alias}");
        _mciOpen = false;
    }

    /// <summary>
    /// Loops <paramref name="filePath"/> on its own MCI alias, independent of the
    /// alarm/music channel above, so a reminder or end-of-session alarm can play over it
    /// without cutting the loop. Unlike macOS's afplay, MCI's own "repeat" flag loops
    /// natively — no relaunch, and so no seam to worry about.
    /// </summary>
    public void PlayAmbient(string filePath, int volume)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return;
        }

        var level = Math.Clamp(volume, TimerConfig.MinVolume, TimerConfig.MaxVolume);

        lock (_ambientGate)
        {
            CloseAmbientMci();

            var deviceType = Path.GetExtension(filePath).Equals(".wav", StringComparison.OrdinalIgnoreCase)
                ? "waveaudio"
                : "mpegvideo";

            if (Send($"open \"{filePath}\" type {deviceType} alias {AmbientAlias}") != 0)
            {
                return;
            }

            _ambientMciOpen = true;

            Send(string.Format(CultureInfo.InvariantCulture, "setaudio {0} volume to {1}", AmbientAlias, level * 10));
            Send($"play {AmbientAlias} repeat");
        }
    }

    public void StopAmbient()
    {
        lock (_ambientGate)
        {
            CloseAmbientMci();
        }
    }

    /// <summary>Must be called under <see cref="_ambientGate"/>.</summary>
    private void CloseAmbientMci()
    {
        if (!_ambientMciOpen)
        {
            return;
        }

        Send($"stop {AmbientAlias}");
        Send($"close {AmbientAlias}");
        _ambientMciOpen = false;
    }

    private static int Send(string command) =>
        mciSendString(command, null, 0, IntPtr.Zero);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(
        string command,
        StringBuilder? returnValue,
        int returnLength,
        IntPtr callback);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(string? pszSound, IntPtr hmod, uint fdwSound);

    private static List<AlarmSound> DiscoverAmbientSounds()
    {
        if (!Directory.Exists(AmbientSoundsDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(AmbientSoundsDirectory, "*.wav")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new AlarmSound(FormatAmbientLabel(path), path))
            .ToList();
    }

    /// <summary>"white-noise.wav" -> "White Noise".</summary>
    private static string FormatAmbientLabel(string path)
    {
        var words = Path.GetFileNameWithoutExtension(path).Split('-', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    public void Dispose()
    {
        Stop();
        StopAmbient();
    }
}
