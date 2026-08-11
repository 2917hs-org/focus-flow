namespace FocusFlow.Application.Interfaces;

/// <summary>
/// A user-selectable alarm sound.
/// </summary>
/// <param name="Name">Label to show in the settings UI.</param>
/// <param name="Value">
/// Platform token stored in <see cref="Domain.Models.TimerConfig.AlarmSoundPath"/> — a
/// file path, or a Windows system-sound alias. Null means the platform default.
/// </param>
public sealed record AlarmSound(string Name, string? Value);

/// <summary>
/// FR-009/FR-010. Plays alarm sounds and music files with volume control.
/// </summary>
/// <remarks>
/// Split out of INotificationService because music playback needs exactly the same
/// capability, and because volume support forces a different API on each platform than
/// the simple "beep" the notification path started with.
/// </remarks>
public interface IAudioPlayer
{
    /// <summary>Built-in sounds for the settings picker. Always contains a default entry.</summary>
    IReadOnlyList<AlarmSound> AvailableSounds { get; }

    /// <summary>
    /// Bundled ambient loop presets — synthesized noise (white/pink/brown), not
    /// recordings, so there's nothing to license. Unlike <see cref="AvailableSounds"/>
    /// there's no "default" entry: ambient sound starts opted out, with nothing selected,
    /// until the user picks one of these or browses to their own file.
    /// </summary>
    IReadOnlyList<AlarmSound> AvailableAmbientSounds { get; }

    /// <summary>File extensions this platform can play, e.g. ".wav", ".mp3".</summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>
    /// Plays a sound token or file path at <paramref name="volume"/> (0-100), falling
    /// back to the platform default when it cannot be played.
    /// </summary>
    void Play(string? sound, int volume);

    /// <summary>Stops anything currently playing via <see cref="Play"/>.</summary>
    void Stop();

    /// <summary>
    /// Starts looping <paramref name="filePath"/> at <paramref name="volume"/> (0-100)
    /// until <see cref="StopAmbient"/> is called, on its own channel independent of
    /// <see cref="Play"/>/<see cref="Stop"/> — an alarm or reminder can play over it
    /// without cutting the loop. Calling this again while already looping restarts the
    /// loop, e.g. to pick up a new file or volume.
    /// </summary>
    void PlayAmbient(string filePath, int volume);

    /// <summary>Stops the ambient loop only, leaving one-shot playback (<see cref="Play"/>) untouched.</summary>
    void StopAmbient();
}
