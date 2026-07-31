using FocusFlow.Application.Interfaces;
using FocusFlow.Domain.Models;

namespace FocusFlow.Application.Services;

/// <summary>
/// Keeps the live settings in memory and writes them back to storage.
/// </summary>
/// <remarks>
/// Saves are debounced: duration edits arrive one keystroke at a time, and writing the
/// file on each one would mean dozens of writes for a single change. A short quiet period
/// collapses those into one.
/// </remarks>
public sealed class SettingsService : ISettingsService, IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly IConfigStorage _storage;
    private readonly ITimer _saveTimer;
    private readonly Lock _gate = new();

    private TimerConfig _current;
    private bool _savePending;
    private bool _disposed;

    public SettingsService(IConfigStorage storage) : this(storage, TimeProvider.System)
    {
    }

    public SettingsService(IConfigStorage storage, TimeProvider timeProvider)
    {
        _storage = storage;
        _current = Load(storage);
        _saveTimer = timeProvider.CreateTimer(
            _ => Flush(),
            state: null,
            dueTime: Timeout.InfiniteTimeSpan,
            period: Timeout.InfiniteTimeSpan);
    }

    public TimerConfig Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event EventHandler<SettingsChangedEventArgs>? Changed;

    public void Update(Action<TimerConfig> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        TimerConfig updated;
        lock (_gate)
        {
            // Mutate a copy so Current is never observed mid-edit, and normalize before
            // it reaches the engine.
            var draft = _current.Clone();
            mutate(draft);
            updated = draft.Normalized();

            if (AreEquivalent(_current, updated))
            {
                return;
            }

            _current = updated;
            _savePending = true;
            _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }

        Changed?.Invoke(this, new SettingsChangedEventArgs(updated));
    }

    public void Flush()
    {
        TimerConfig toSave;
        lock (_gate)
        {
            if (_disposed || !_savePending)
            {
                return;
            }

            _savePending = false;
            _saveTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            toSave = _current;
        }

        try
        {
            _storage.Save(toSave);
        }
        catch (Exception)
        {
            // Losing a settings write must not take the app down mid-session.
        }
    }

    private static TimerConfig Load(IConfigStorage storage)
    {
        try
        {
            return storage.Load().Normalized();
        }
        catch (Exception)
        {
            return new TimerConfig();
        }
    }

    private static bool AreEquivalent(TimerConfig a, TimerConfig b) =>
        a.StudyDuration == b.StudyDuration
        && a.BreakDuration == b.BreakDuration
        && a.AutoStartBreak == b.AutoStartBreak
        && a.AutoStartStudy == b.AutoStartStudy
        && a.AlarmSoundPath == b.AlarmSoundPath;

    public void Dispose()
    {
        Flush();

        lock (_gate)
        {
            _disposed = true;
            _saveTimer.Dispose();
        }
    }
}
