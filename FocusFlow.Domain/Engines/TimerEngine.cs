using FocusFlow.Domain.Interfaces;
using FocusFlow.Domain.Models;

namespace FocusFlow.Domain.Engines;

/// <summary>
/// Pomodoro-style countdown that alternates study and break sessions.
/// </summary>
/// <remarks>
/// <para>
/// Time is derived from a monotonic timestamp taken when the current segment started,
/// not accumulated by subtracting a second per tick. Polling faster than once a second
/// and recomputing from the start timestamp means a delayed or coalesced timer callback
/// cannot make the countdown drift, and it stays correct if the wall clock changes.
/// </para>
/// <para>
/// The <see cref="TimeProvider"/> is injected so tests can advance time deterministically
/// with FakeTimeProvider instead of sleeping.
/// </para>
/// </remarks>
public sealed class TimerEngine : ITimerEngine, IDisposable
{
    /// <summary>
    /// Polling cadence. Well under a second so the displayed value flips close to the
    /// real boundary; <see cref="Tick"/> is still only raised when the second changes.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// FR-101. A gap this much larger than <see cref="PollInterval"/> means the poll was
    /// not merely late but starved — in practice, the machine was suspended. Ordinary
    /// scheduler pressure delays a 200ms timer by milliseconds, never by seconds.
    /// </summary>
    private static readonly TimeSpan SuspendThreshold = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();

    private TimerConfig _config = new();
    private ITimer? _timer;

    private TimerMode _mode = TimerMode.Idle;
    private bool _isPaused;
    private int _currentSession = 1;

    /// <summary>
    /// True when the current break was started on its own via <see cref="StartBreak"/>
    /// rather than as part of a study cycle, so finishing it ends the run instead of
    /// rolling into a study session the user never asked for.
    /// </summary>
    private bool _standaloneBreak;

    /// <summary>Timestamp when the running segment began.</summary>
    private long _segmentStart;

    /// <summary>Time left when the running segment began; the whole remainder when paused.</summary>
    private TimeSpan _segmentRemaining;

    /// <summary>UTC instant the current session began, recorded for the history log.</summary>
    private DateTimeOffset _sessionStartedAt;

    /// <summary>Full configured length of the current session, for the history log.</summary>
    private TimeSpan _sessionPlanned;

    /// <summary>Last whole second handed to <see cref="Tick"/>, to avoid duplicate events.</summary>
    private TimeSpan _lastRaised = TimeSpan.MinValue;

    /// <summary>Timestamp of the previous poll, used to spot a suspend gap (FR-101).</summary>
    private long _lastPoll;

    /// <summary>Whether the pre-end reminder has already fired for this session.</summary>
    private bool _reminderFired;

    private bool _disposed;

    public TimerEngine() : this(TimeProvider.System)
    {
    }

    public TimerEngine(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public event EventHandler<TimerTickEventArgs>? Tick;
    public event EventHandler<SessionEndedEventArgs>? SessionEnded;
    public event EventHandler<SystemResumedEventArgs>? SystemResumed;
    public event EventHandler<ReminderDueEventArgs>? ReminderDue;

    public SessionState CurrentState
    {
        get
        {
            lock (_gate)
            {
                return Snapshot();
            }
        }
    }

    /// <summary>
    /// Starts a fresh run at session 1. Calling this while already running restarts with
    /// the new config rather than throwing — the UI can hand us a config at any time.
    /// </summary>
    public void Start(TimerConfig config) => BeginRun(config, TimerMode.Study, standalone: false);

    /// <summary>FR-002. Runs a single break with no study session attached.</summary>
    public void StartBreak(TimerConfig config) => BeginRun(config, TimerMode.Break, standalone: true);

    private void BeginRun(TimerConfig config, TimerMode mode, bool standalone)
    {
        ArgumentNullException.ThrowIfNull(config);

        SessionState snapshot;
        lock (_gate)
        {
            _config = config;
            _mode = mode;
            _currentSession = 1;
            _isPaused = false;
            _standaloneBreak = standalone;
            _lastRaised = TimeSpan.MinValue;
            BeginSegment(DurationFor(mode));
            EnsureTimer(active: true);
            snapshot = SnapshotAndMark();
        }

        RaiseTick(snapshot);
    }

    /// <summary>
    /// FR-013. Reinstates a run saved by a previous launch. Always comes back paused: the
    /// wall-clock time between the crash and the restart is not study time, and silently
    /// resuming would either burn or credit minutes the user never sat through.
    /// </summary>
    public void Restore(TimerConfig config, SessionState state)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(state);

        if (state.Mode == TimerMode.Idle)
        {
            return;
        }

        SessionState snapshot;
        lock (_gate)
        {
            _config = config;
            _mode = state.Mode;
            _currentSession = Math.Max(1, state.CurrentSession);
            _isPaused = true;
            _standaloneBreak = false;
            _lastRaised = TimeSpan.MinValue;

            _segmentRemaining = state.RemainingTime > TimeSpan.Zero
                ? state.RemainingTime
                : DurationFor(state.Mode);
            _segmentStart = _timeProvider.GetTimestamp();
            _sessionStartedAt = _timeProvider.GetUtcNow();
            _sessionPlanned = DurationFor(state.Mode);

            EnsureTimer(active: false);
            snapshot = SnapshotAndMark();
        }

        RaiseTick(snapshot);
    }

    public void Pause()
    {
        SessionState snapshot;
        lock (_gate)
        {
            if (_mode == TimerMode.Idle || _isPaused)
            {
                return;
            }

            // Freeze what is left, then stop the clock. Recomputing from _segmentStart
            // is what makes a pause/resume cycle lossless.
            _segmentRemaining = ComputeRemaining();
            _isPaused = true;
            EnsureTimer(active: false);
            snapshot = SnapshotAndMark();
        }

        RaiseTick(snapshot);
    }

    public void Resume()
    {
        SessionState snapshot;
        lock (_gate)
        {
            if (_mode == TimerMode.Idle || !_isPaused)
            {
                return;
            }

            _isPaused = false;
            _segmentStart = _timeProvider.GetTimestamp();
            EnsureTimer(active: true);
            snapshot = SnapshotAndMark();
        }

        RaiseTick(snapshot);
    }

    public void Stop()
    {
        SessionState snapshot;
        SessionEndedEventArgs? ended = null;

        lock (_gate)
        {
            if (_mode != TimerMode.Idle)
            {
                // Time already spent is still time spent — report it so the history log
                // records the partial session instead of silently discarding it.
                var completed = _mode;
                var startedAt = _sessionStartedAt;
                var planned = _sessionPlanned;
                var number = _currentSession;
                var actual = Spent();

                GoIdle();
                ended = new SessionEndedEventArgs(
                    completed, TimerMode.Idle, Snapshot(),
                    SessionOutcome.Stopped, startedAt, planned, actual, number);
            }
            else
            {
                GoIdle();
            }

            snapshot = SnapshotAndMark();
        }

        if (ended is not null)
        {
            SessionEnded?.Invoke(this, ended);
        }

        RaiseTick(snapshot);
    }

    public void Reset()
    {
        SessionState snapshot;
        lock (_gate)
        {
            // Read the mode *before* changing anything. The previous implementation
            // stopped first — which set Mode to Idle — and so always reset to the break
            // duration regardless of which session was actually running.
            if (_mode == TimerMode.Idle)
            {
                _mode = TimerMode.Study;
            }

            _isPaused = true;
            _lastRaised = TimeSpan.MinValue;
            BeginSegment(DurationFor(_mode));
            EnsureTimer(active: false);
            snapshot = SnapshotAndMark();
        }

        RaiseTick(snapshot);
    }

    /// <summary>
    /// FR-005. Ends the current session now and starts the next one running, regardless of
    /// the auto-start settings — skipping is an explicit manual advance, so stopping at a
    /// paused next session would just need a second click.
    /// </summary>
    public void Skip()
    {
        SessionState snapshot;
        SessionEndedEventArgs ended;

        lock (_gate)
        {
            if (_mode == TimerMode.Idle)
            {
                return;
            }

            ended = AdvanceSession(SessionOutcome.Skipped, forceStart: true);
            snapshot = SnapshotAndMark();
        }

        SessionEnded?.Invoke(this, ended);
        RaiseTick(snapshot);
    }

    private void OnTimerCallback()
    {
        SessionState snapshot;
        SessionEndedEventArgs? ended = null;
        SystemResumedEventArgs? resumed = null;
        ReminderDueEventArgs? reminder = null;

        lock (_gate)
        {
            if (_disposed || _mode == TimerMode.Idle || _isPaused)
            {
                return;
            }

            var gap = _timeProvider.GetElapsedTime(_lastPoll);
            _lastPoll = _timeProvider.GetTimestamp();

            if (gap > SuspendThreshold)
            {
                // Read this *before* crediting the gap back, while ComputeRemaining still
                // reflects the slept-through time.
                var wouldHaveEnded = ComputeRemaining() <= TimeSpan.Zero;

                // FR-101. Sleeping is not studying: push the segment start forward by the
                // gap so the suspended time is never charged to the session. On platforms
                // whose monotonic clock already freezes during sleep this is a no-op in
                // effect; on those where it keeps running it is what stops a nap from
                // silently burning through the whole session.
                _segmentStart += (long)(gap.TotalSeconds * _timeProvider.TimestampFrequency);

                // A session that was nearly over before the machine slept should not have
                // its reminder fire late, on the far side of the nap.
                if (wouldHaveEnded)
                {
                    _reminderFired = true;
                }

                resumed = new SystemResumedEventArgs(gap, Snapshot(), wouldHaveEnded);
            }

            var remaining = ComputeRemaining();

            if (!_reminderFired
                && _config.ReminderEnabled
                && _config.ReminderLeadTime < _sessionPlanned
                && remaining > TimeSpan.Zero
                && remaining <= _config.ReminderLeadTime)
            {
                _reminderFired = true;
                reminder = new ReminderDueEventArgs(_mode, remaining, Snapshot());
            }

            if (remaining <= TimeSpan.Zero)
            {
                ended = AdvanceSession(SessionOutcome.Completed, forceStart: false);
            }

            snapshot = Snapshot();

            // Only surface a tick when the displayed second actually changed, so the
            // 200ms poll does not spam the UI with five identical updates per second.
            // A wake still has to get through: crediting the slept time back leaves the
            // remaining time identical, so dedup alone would silently drop the event.
            if (ended is null && resumed is null && reminder is null
                && snapshot.RemainingTime == _lastRaised)
            {
                return;
            }

            _lastRaised = snapshot.RemainingTime;
        }

        // Events go out with the lock released: handlers run arbitrary UI code and must
        // never be able to deadlock the engine or re-enter it under the lock.
        if (resumed is not null)
        {
            SystemResumed?.Invoke(this, resumed);
        }

        if (reminder is not null)
        {
            ReminderDue?.Invoke(this, reminder);
        }

        if (ended is not null)
        {
            SessionEnded?.Invoke(this, ended);
        }

        Tick?.Invoke(this, new TimerTickEventArgs(snapshot));
    }

    /// <summary>Rolls over to the next session. Must be called under <see cref="_gate"/>.</summary>
    private SessionEndedEventArgs AdvanceSession(SessionOutcome outcome, bool forceStart)
    {
        // Snapshot the finishing session first: BeginSegment below overwrites the fields
        // the history record is built from.
        var completed = _mode;
        var startedAt = _sessionStartedAt;
        var planned = _sessionPlanned;
        var number = _currentSession;
        var actual = outcome == SessionOutcome.Completed ? planned : Spent();

        var next = completed == TimerMode.Study ? TimerMode.Break : TimerMode.Study;

        if (next == TimerMode.Study)
        {
            // A break that was started on its own has nothing to return to, and a finite
            // run stops once the configured number of study sessions is done (FR-007).
            var runFinished = _standaloneBreak
                || (!_config.InfiniteMode && _currentSession >= _config.SessionCount);

            if (runFinished)
            {
                GoIdle();
                return new SessionEndedEventArgs(
                    completed, TimerMode.Idle, Snapshot(),
                    outcome, startedAt, planned, actual, number);
            }

            _currentSession++;
        }

        _mode = next;
        BeginSegment(DurationFor(next));

        var autoStart = next == TimerMode.Break
            ? _config.AutoStartBreak
            : _config.AutoStartStudy;

        if (!autoStart && !forceStart)
        {
            // Sit at the full next duration until the user starts it themselves.
            _isPaused = true;
            EnsureTimer(active: false);
        }
        else
        {
            _isPaused = false;

            // Re-phase the poll onto the new segment. The interval otherwise keeps its
            // original alignment, so the next session's seconds would flip up to one
            // poll late for its whole duration.
            EnsureTimer(active: true);
        }

        return new SessionEndedEventArgs(
            completed, next, Snapshot(), outcome, startedAt, planned, actual, number);
    }

    /// <summary>Time spent in the current session so far. Call under <see cref="_gate"/>.</summary>
    private TimeSpan Spent()
    {
        var spent = _sessionPlanned - ComputeRemaining();
        return spent < TimeSpan.Zero ? TimeSpan.Zero : spent;
    }

    /// <summary>Returns to the idle state. Must be called under <see cref="_gate"/>.</summary>
    private void GoIdle()
    {
        _mode = TimerMode.Idle;
        _isPaused = false;
        _standaloneBreak = false;
        _currentSession = 1;
        _segmentRemaining = TimeSpan.Zero;
        _lastRaised = TimeSpan.MinValue;
        EnsureTimer(active: false);
    }

    private TimeSpan DurationFor(TimerMode mode) =>
        mode == TimerMode.Break ? _config.BreakDuration : _config.StudyDuration;

    private void BeginSegment(TimeSpan duration)
    {
        _segmentRemaining = duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
        _segmentStart = _timeProvider.GetTimestamp();
        _sessionStartedAt = _timeProvider.GetUtcNow();
        _sessionPlanned = _segmentRemaining;
        _reminderFired = false;
    }

    private TimeSpan ComputeRemaining()
    {
        if (_mode == TimerMode.Idle || _isPaused)
        {
            return _segmentRemaining;
        }

        // Clamp elapsed at zero as well: a monotonic source should never run backwards,
        // but if one ever did, a negative elapsed would silently inflate the session.
        var elapsed = _timeProvider.GetElapsedTime(_segmentStart);
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var remaining = _segmentRemaining - elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>
    /// Creates the timer on first use, then parks or un-parks it. Parking via Change
    /// rather than Dispose is what makes this safe to call from inside the callback.
    /// </summary>
    private void EnsureTimer(bool active)
    {
        if (_disposed)
        {
            return;
        }

        if (active)
        {
            _timer ??= _timeProvider.CreateTimer(
                _ => OnTimerCallback(),
                state: null,
                dueTime: PollInterval,
                period: PollInterval);

            _lastPoll = _timeProvider.GetTimestamp();
            _timer.Change(PollInterval, PollInterval);
        }
        else
        {
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Snapshots and records the displayed second in one step, so the tick-dedup state is
    /// only ever written under <see cref="_gate"/>. Must be called under the lock.
    /// </summary>
    private SessionState SnapshotAndMark()
    {
        var snapshot = Snapshot();
        _lastRaised = snapshot.RemainingTime;
        return snapshot;
    }

    /// <summary>Builds a snapshot. Must be called under <see cref="_gate"/>.</summary>
    private SessionState Snapshot()
    {
        var remaining = ComputeRemaining();

        return new SessionState
        {
            Mode = _mode,
            // Round up so a fresh 25-minute session reads "25:00" rather than "24:59",
            // and only reads "00:00" once the session is genuinely over.
            RemainingTime = TimeSpan.FromSeconds(Math.Ceiling(remaining.TotalSeconds)),
            CurrentSession = _currentSession,
            IsPaused = _isPaused,
            LastUpdateTime = _timeProvider.GetUtcNow()
        };
    }

    private void RaiseTick(SessionState snapshot) =>
        Tick?.Invoke(this, new TimerTickEventArgs(snapshot));

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
