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
    private readonly IUserAlerts? _alerts;
    private readonly ITimer _saveTimer;
    private readonly Lock _gate = new();

    private TimerConfig _current;
    private bool _savePending;
    private bool _disposed;

    public SettingsService(IConfigStorage storage) : this(storage, TimeProvider.System)
    {
    }

    /// <param name="alerts">
    /// Optional so tests can construct the service without a UI; null simply means
    /// failures stay silent, which is the old behaviour.
    /// </param>
    public SettingsService(IConfigStorage storage, TimeProvider timeProvider, IUserAlerts? alerts = null)
    {
        _storage = storage;
        _alerts = alerts;
        _current = Load(storage, alerts);
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
        catch (Exception e)
        {
            // Still swallowed so a failed write cannot interrupt a running session — but
            // no longer silently. Without this the user changes a setting, it appears to
            // take, and it is gone at next launch with nothing to explain why.
            _alerts?.Report(
                "settings-save",
                "FocusFlow can't save your settings",
                "Your changes apply to this session but won't be remembered next time. "
                + "FocusFlow needs permission to write to its data folder.\n\n"
                + e.Message);
        }
    }

    private static TimerConfig Load(IConfigStorage storage, IUserAlerts? alerts)
    {
        try
        {
            return storage.Load().Normalized();
        }
        catch (Exception e)
        {
            alerts?.Report(
                "settings-load",
                "FocusFlow couldn't read your settings",
                "Default settings have been restored. Your previous preferences may have "
                + "been lost.\n\n" + e.Message);
            return new TimerConfig();
        }
    }

    private static bool AreEquivalent(TimerConfig a, TimerConfig b) =>
        a.StudyDuration == b.StudyDuration
        && a.BreakDuration == b.BreakDuration
        && a.AutoStartBreak == b.AutoStartBreak
        && a.AutoStartStudy == b.AutoStartStudy
        && a.AlarmSoundPath == b.AlarmSoundPath
        && a.IdleAutoPauseEnabled == b.IdleAutoPauseEnabled
        && a.IdleAutoPauseThreshold == b.IdleAutoPauseThreshold
        && a.BlockedAppIds.SequenceEqual(b.BlockedAppIds, StringComparer.OrdinalIgnoreCase)
        // Record equality, so this is a value comparison, not a reference one — required
        // here: without it, an update that changes only a hotkey binding would look like a
        // no-op and never persist, even though IGlobalHotkeys.Apply already took effect.
        && a.StartPauseHotkey == b.StartPauseHotkey
        && a.StopHotkey == b.StopHotkey
        && a.SkipHotkey == b.SkipHotkey;

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
