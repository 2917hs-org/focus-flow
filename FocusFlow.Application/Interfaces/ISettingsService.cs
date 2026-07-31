using FocusFlow.Domain.Models;

namespace FocusFlow.Application.Interfaces;

public sealed class SettingsChangedEventArgs : EventArgs
{
    public SettingsChangedEventArgs(TimerConfig config)
    {
        Config = config;
    }

    public TimerConfig Config { get; }
}

/// <summary>
/// Holds the live <see cref="TimerConfig"/> and persists edits.
/// </summary>
public interface ISettingsService
{
    /// <summary>The current settings. Never mutate this — go through <see cref="Update"/>.</summary>
    TimerConfig Current { get; }

    event EventHandler<SettingsChangedEventArgs>? Changed;

    /// <summary>Applies an edit and schedules a save.</summary>
    void Update(Action<TimerConfig> mutate);

    /// <summary>Writes any pending save immediately, e.g. on shutdown.</summary>
    void Flush();
}
