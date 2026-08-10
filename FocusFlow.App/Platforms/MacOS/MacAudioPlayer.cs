using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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

    /// <summary>Where FocusFlow.App.csproj copies Assets/Ambient/** at publish time — see
    /// its comment for why these ship as loose files instead of AvaloniaResource.</summary>
    private static readonly string AmbientSoundsDirectory =
        Path.Combine(AppContext.BaseDirectory, "Assets", "Ambient");

    private static readonly string[] Extensions = [".wav", ".mp3", ".aiff", ".aif", ".m4a", ".caf"];

    private readonly Lazy<IReadOnlyList<AlarmSound>> _sounds = new(DiscoverSounds);
    private readonly Lazy<IReadOnlyList<AlarmSound>> _ambientSounds = new(DiscoverAmbientSounds);
    private readonly Lock _gate = new();
    private readonly Lock _ambientGate = new();
    private readonly IAppLogger? _logger;

    private Process? _current;

    /// <summary>
    /// <summary>
    /// How long before a loop iteration's estimated natural end the next one is
    /// pre-launched, so the two overlap briefly instead of leaving a silent gap. The
    /// earlier reactive-only relaunch (fire only once afplay actually exits) measured a
    /// ~20-30ms gap end-to-end; this constant only needs to cover that plus afplay's own
    /// CoreAudio startup latency to turn it into an overlap instead. In practice the
    /// achieved overlap runs longer than that — afplay's measured process lifetime is
    /// consistently ~1s more than afinfo's own reported duration for that same file (own
    /// startup/shutdown overhead this class has no way to measure separately from actual
    /// audio-level playback), so this lead time is a lower bound on the real overlap, not
    /// a precise target. That's the safe direction to be imprecise in: for a continuous
    /// noise/rain texture, an overlap longer than intended reads as nothing in particular,
    /// where a gap would read as a click.
    /// </summary>
    private static readonly TimeSpan AmbientPrelaunchLead = TimeSpan.FromMilliseconds(300);

    private readonly ConcurrentDictionary<string, double?> _ambientDurationCache = new();

    // afplay has no native loop flag, unlike Windows MCI's "play alias repeat". Looping is
    // implemented by relaunching afplay — pre-emptively, just before the current
    // iteration's estimated end (RelaunchAmbientLocked via the prelaunch Timer), so the
    // old and new processes briefly overlap instead of leaving a gap; the old one is left
    // to exit on its own rather than killed, which is what makes the overlap happen. If
    // the duration can't be determined (afinfo failure/unsupported format) or the natural
    // exit fires before the timer does (an underestimate), the reactive Process.Exited
    // handler relaunches instead — same as before this file had prelaunch scheduling at
    // all, just now a fallback rather than the only path.
    //
    // _ambientGeneration guards both trigger paths against acting twice for the same loop
    // iteration, and against a stale event/timer from an already-superseded process/file
    // (Process.Exited fires asynchronously, after the lock that concluded that generation
    // has been released) doing anything once StopAmbient/PlayAmbient has moved on.
    private Process? _ambientCurrent;
    private Timer? _ambientPrelaunchTimer;
    private string? _ambientPath;
    private double _ambientGain;
    private bool _ambientLooping;
    private int _ambientGeneration;

    public MacAudioPlayer(IAppLogger? logger = null)
    {
        _logger = logger;
    }

    public IReadOnlyList<AlarmSound> AvailableSounds => _sounds.Value;

    public IReadOnlyList<AlarmSound> AvailableAmbientSounds => _ambientSounds.Value;

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

    public void PlayAmbient(string filePath, int volume)
    {
        if (!OperatingSystem.IsMacOS() || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return;
        }

        var gain = Math.Clamp(volume, TimerConfig.MinVolume, TimerConfig.MaxVolume) / 100.0;

        lock (_ambientGate)
        {
            _ambientPath = filePath;
            _ambientGain = gain;
            _ambientLooping = true;

            // A cold (re)start always cuts over hard — unlike the loop's own internal
            // relaunch, there's a real reason to kill here: this may be a *different*
            // file/volume than whatever was playing, and two different ambient tracks
            // briefly overlapping would sound like a mess rather than a seamless loop.
            KillAmbientProcessAndTimerLocked();
            RelaunchAmbientLocked();
        }
    }

    public void StopAmbient()
    {
        lock (_ambientGate)
        {
            _ambientLooping = false;
            _ambientGeneration++;
            KillAmbientProcessAndTimerLocked();
        }
    }

    /// <summary>
    /// Starts the next loop iteration under a fresh generation. Deliberately does *not*
    /// touch whatever is in <see cref="_ambientCurrent"/> — when called from the prelaunch
    /// timer (the common case) or a natural Process.Exited (the fallback case), that's the
    /// still-finishing previous iteration of the *same* file, which is left to exit on its
    /// own so the two overlap instead of leaving a gap. Callers that actually want a hard
    /// cut (<see cref="PlayAmbient"/>, <see cref="StopAmbient"/>) call
    /// <see cref="KillAmbientProcessAndTimerLocked"/> themselves first. Must be called
    /// under <see cref="_ambientGate"/>.
    /// </summary>
    private void RelaunchAmbientLocked()
    {
        var generation = ++_ambientGeneration;

        Process process;
        try
        {
            var startInfo = new ProcessStartInfo("afplay")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-v");
            startInfo.ArgumentList.Add(_ambientGain.ToString("0.###", CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(_ambientPath!);

            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Exited += (_, _) => OnAmbientExited(generation, process);
            process.Start();
        }
        catch (Exception e)
        {
            // A failed ambient loop must never take the timer down with it.
            _logger?.Warn($"afplay failed to start ambient loop: {e.Message}");
            return;
        }

        _ambientCurrent = process;
        _ambientPrelaunchTimer?.Dispose();
        _ambientPrelaunchTimer = null;

        var duration = GetAmbientDurationSeconds(_ambientPath!);
        if (duration is { } seconds && seconds > AmbientPrelaunchLead.TotalSeconds)
        {
            var due = TimeSpan.FromSeconds(seconds) - AmbientPrelaunchLead;
            _ambientPrelaunchTimer = new Timer(_ => OnAmbientPrelaunchDue(generation), null, due, Timeout.InfiniteTimeSpan);
        }
        // Else: duration unknown, or too short to leave room for a lead-in — the reactive
        // Process.Exited handler below is the only relaunch trigger for this iteration,
        // same as before prelaunch scheduling existed at all.
    }

    /// <summary>
    /// Fires shortly before afplay is expected to finish on its own, to pre-launch the
    /// next iteration while this one is still audible. Runs on a <see cref="Timer"/>
    /// thread-pool thread.
    /// </summary>
    private void OnAmbientPrelaunchDue(int generation)
    {
        lock (_ambientGate)
        {
            // Superseded already — StopAmbient/PlayAmbient moved on, or the natural exit
            // fired first (the duration estimate came in long) and already relaunched.
            if (!_ambientLooping || generation != _ambientGeneration)
            {
                return;
            }

            RelaunchAmbientLocked();
        }
    }

    /// <summary>
    /// Fires on the .NET thread pool once afplay exits on its own — the expected trigger
    /// when the duration couldn't be determined, or a safety net if the prelaunch timer's
    /// estimate ran long. Also fires for a process <see cref="KillAmbientProcessAndTimerLocked"/>
    /// killed outright; the generation check tells the two apart, since a kill bumps
    /// <see cref="_ambientGeneration"/> before this can observe it. Either way,
    /// <paramref name="exitedProcess"/> — not <see cref="_ambientCurrent"/>, which may
    /// already refer to a newer process by the time this runs — is what gets disposed, so
    /// a superseded process handle doesn't leak.
    /// </summary>
    private void OnAmbientExited(int generation, Process exitedProcess)
    {
        lock (_ambientGate)
        {
            if (!_ambientLooping || generation != _ambientGeneration)
            {
                exitedProcess.Dispose();
                return;
            }

            RelaunchAmbientLocked();
        }
    }

    /// <summary>Must be called under <see cref="_ambientGate"/>.</summary>
    private void KillAmbientProcessAndTimerLocked()
    {
        _ambientPrelaunchTimer?.Dispose();
        _ambientPrelaunchTimer = null;

        try
        {
            if (_ambientCurrent is { HasExited: false })
            {
                _ambientCurrent.Kill(entireProcessTree: true);
            }

            _ambientCurrent?.Dispose();
        }
        catch (Exception e)
        {
            _logger?.Warn($"Stopping ambient afplay failed: {e.Message}");
        }
        finally
        {
            _ambientCurrent = null;
        }
    }

    /// <summary>
    /// Probes and caches a file's duration via afinfo (ships with macOS, same family as
    /// afplay — no new dependency). Cached per path so a multi-minute loop doesn't shell
    /// out again on every single iteration; null (probe failed, or an unrecognised format)
    /// permanently falls back to reactive-only relaunching for that file.
    /// </summary>
    private double? GetAmbientDurationSeconds(string path) =>
        _ambientDurationCache.GetOrAdd(path, ProbeDurationSeconds);

    private static double? ProbeDurationSeconds(string path)
    {
        try
        {
            var startInfo = new ProcessStartInfo("afinfo")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(path);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);

            var match = Regex.Match(output, @"estimated duration:\s*([\d.]+)\s*sec");
            return match.Success
                && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                ? seconds
                : null;
        }
        catch
        {
            // afinfo missing, the file unreadable/unrecognised, etc. — the caller falls
            // back to reactive relaunching, which is strictly no worse than before this
            // file had prelaunch scheduling at all.
            return null;
        }
    }

    private static List<AlarmSound> DiscoverSounds()
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
