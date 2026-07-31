using FocusFlow.Application.Interfaces;
using FocusFlow.Domain.Interfaces;
using FocusFlow.Domain.Models;

namespace FocusFlow.Application.Services;

/// <summary>
/// FR-013. Mirrors the running session to disk so a crash, a forced quit or a lost power
/// cable doesn't discard it.
/// </summary>
/// <remarks>
/// Writes are throttled rather than written on every tick: a per-second write would mean
/// thousands of tiny disk writes per session for state that only needs to be roughly
/// current. Session transitions are always written immediately, since those are the
/// points where losing state actually costs the user something.
/// </remarks>
public sealed class SessionPersistenceService : IDisposable
{
    private static readonly TimeSpan WriteInterval = TimeSpan.FromSeconds(5);

    private readonly ITimerService _timerService;
    private readonly ISessionStateStorage _storage;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();

    private long _lastWrite;
    private bool _started;
    private bool _disposed;

    public SessionPersistenceService(
        ITimerService timerService,
        ISessionStateStorage storage,
        TimeProvider timeProvider)
    {
        _timerService = timerService;
        _storage = storage;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Reinstates a previous run if one was saved, then begins mirroring. Returns true
    /// when a session was restored.
    /// </summary>
    public bool StartTracking(TimerConfig config)
    {
        var restored = false;

        try
        {
            var saved = _storage.Load();
            if (saved is not null && saved.Mode != TimerMode.Idle)
            {
                _timerService.Restore(config, saved);
                restored = true;
            }
        }
        catch (Exception)
        {
            // A damaged state file should cost the resume, not the launch.
        }

        lock (_gate)
        {
            if (_started)
            {
                return restored;
            }

            _started = true;
            _lastWrite = _timeProvider.GetTimestamp();
        }

        _timerService.TimerUpdated += OnTimerUpdated;
        _timerService.SessionEnded += OnSessionEnded;
        return restored;
    }

    private void OnSessionEnded(object? sender, SessionEndedEventArgs e) => Write(e.State, force: true);

    private void OnTimerUpdated(object? sender, TimerUpdatedEventArgs e) => Write(e.State, force: false);

    private void Write(SessionState state, bool force)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (state.Mode == TimerMode.Idle)
            {
                // Nothing in flight — drop the file so the next launch starts clean
                // rather than offering to resume a run that already finished.
                TryClear();
                return;
            }

            if (!force && _timeProvider.GetElapsedTime(_lastWrite) < WriteInterval)
            {
                return;
            }

            _lastWrite = _timeProvider.GetTimestamp();
            TrySave(state);
        }
    }

    private void TrySave(SessionState state)
    {
        try
        {
            _storage.Save(state);
        }
        catch (Exception)
        {
            // Best effort: never let a failed snapshot interrupt the session.
        }
    }

    private void TryClear()
    {
        try
        {
            _storage.Clear();
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Writes the current state immediately, e.g. on shutdown.</summary>
    public void Flush()
    {
        if (_disposed)
        {
            return;
        }

        Write(_timerService.CurrentState, force: true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Flush();

        lock (_gate)
        {
            _disposed = true;
        }

        _timerService.TimerUpdated -= OnTimerUpdated;
        _timerService.SessionEnded -= OnSessionEnded;
    }
}
