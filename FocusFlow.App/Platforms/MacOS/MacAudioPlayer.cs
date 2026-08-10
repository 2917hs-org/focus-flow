using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using FocusFlow.Application.Interfaces;
using FocusFlow.Domain.Models;

namespace FocusFlow.App.Platforms.MacOS;

/// <summary>
/// FR-009/FR-010 on macOS, via afplay.
/// </summary>
/// <remarks>
/// afplay ships with the OS and already handles WAV, MP3, AIFF and M4A, and its -v flag
/// gives volume control — so no third-party audio library is needed here.
/// </remarks>
public sealed class MacAudioPlayer : IAudioPlayer, IDisposable
{
    private const string SoundsDirectory = "/System/Library/Sounds";
    private const string DefaultSound = SoundsDirectory + "/Glass.aiff";

    private static readonly string[] Extensions = [".wav", ".mp3", ".aiff", ".aif", ".m4a", ".caf"];

    private readonly Lazy<IReadOnlyList<AlarmSound>> _sounds = new(DiscoverSounds);
    private readonly Lock _gate = new();
    private readonly IAppLogger? _logger;

    private Process? _current;

    public MacAudioPlayer(IAppLogger? logger = null)
    {
        _logger = logger;
    }

    public IReadOnlyList<AlarmSound> AvailableSounds => _sounds.Value;

    public IReadOnlyList<string> SupportedExtensions => Extensions;

    public void Play(string? sound, int volume)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        // Fall back to the default if the stored sound has since been moved or deleted,
        // so a stale config file cannot leave the user with a silent alarm.
        var path = !string.IsNullOrWhiteSpace(sound) && File.Exists(sound) ? sound : DefaultSound;

        // afplay's -v is a linear gain where 1.0 is unmodified playback.
        var gain = Math.Clamp(volume, TimerConfig.MinVolume, TimerConfig.MaxVolume) / 100.0;

        Stop();

        try
        {
            var startInfo = new ProcessStartInfo("afplay")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-v");
            startInfo.ArgumentList.Add(gain.ToString("0.###", CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(path);

            lock (_gate)
            {
                _current = Process.Start(startInfo);
            }
        }
        catch (Exception e)
        {
            // A failed alarm must never take the timer down with it.
            _logger?.Warn($"afplay failed to start: {e.Message}");
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            try
            {
                if (_current is { HasExited: false })
                {
                    _current.Kill(entireProcessTree: true);
                }

                _current?.Dispose();
            }
            catch (Exception e)
            {
                // Already gone, or never started.
                _logger?.Warn($"Stopping afplay failed: {e.Message}");
            }
            finally
            {
                _current = null;
            }
        }
    }

    private static IReadOnlyList<AlarmSound> DiscoverSounds()
    {
        var sounds = new List<AlarmSound> { new("Default (Glass)", null) };

        if (!OperatingSystem.IsMacOS() || !Directory.Exists(SoundsDirectory))
        {
            return sounds;
        }

        sounds.AddRange(
            Directory.EnumerateFiles(SoundsDirectory, "*.aiff")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => new AlarmSound(Path.GetFileNameWithoutExtension(path), path)));

        return sounds;
    }

    public void Dispose() => Stop();
}
