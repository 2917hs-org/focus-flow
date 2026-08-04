using FocusFlow.Domain.Models;

namespace FocusFlow.Domain.Interfaces;

/// <summary>Raised once per displayed second so the UI and tray can follow the countdown.</summary>
public sealed class TimerTickEventArgs : EventArgs
{
    public TimerTickEventArgs(SessionState state)
    {
        State = state;
    }

    public SessionState State { get; }
}

/// <summary>Raised when a study or break session finishes.</summary>
public sealed class SessionEndedEventArgs : EventArgs
{
    public SessionEndedEventArgs(
        TimerMode completedMode,
        TimerMode nextMode,
        SessionState state,
        SessionOutcome outcome,
        DateTimeOffset startedAt,
        TimeSpan plannedDuration,
        TimeSpan actualDuration,
        int sessionNumber)
    {
        CompletedMode = completedMode;
        NextMode = nextMode;
        State = state;
        Outcome = outcome;
        StartedAt = startedAt;
        PlannedDuration = plannedDuration;
        ActualDuration = actualDuration;
        SessionNumber = sessionNumber;
    }

    /// <summary>The session that just finished — what the alert should talk about.</summary>
    public TimerMode CompletedMode { get; }

    /// <summary>
    /// What runs next. <see cref="TimerMode.Idle"/> means the configured session count
    /// was reached and the run is over.
    /// </summary>
    public TimerMode NextMode { get; }

    /// <summary>State after the transition, already advanced to <see cref="NextMode"/>.</summary>
    public SessionState State { get; }

    public SessionOutcome Outcome { get; }

    /// <summary>UTC instant the finished session began — for the history log.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>What the finished session was configured to run for.</summary>
    public TimeSpan PlannedDuration { get; }

    /// <summary>What was actually spent in it.</summary>
    public TimeSpan ActualDuration { get; }

    /// <summary>Which session of the run just finished.</summary>
    public int SessionNumber { get; }

    /// <summary>
    /// True when the user pressed Skip rather than the session running out. Consumers use
    /// this to suppress the alarm — an alert for something you deliberately skipped is noise.
    /// </summary>
    public bool WasSkipped => Outcome == SessionOutcome.Skipped;

    /// <summary>True when the whole run finished (FR-007's session count was reached).</summary>
    public bool RunCompleted => NextMode == TimerMode.Idle;

    /// <summary>Projects the finished session into a history record.</summary>
    public SessionRecord ToRecord(DateTimeOffset endedAt) => new()
    {
        Mode = CompletedMode,
        Outcome = Outcome,
        StartedAt = StartedAt,
        EndedAt = endedAt,
        PlannedDuration = PlannedDuration,
        ActualDuration = ActualDuration,
        SessionNumber = SessionNumber
    };
}

/// <summary>
/// FR-101. Raised when the engine notices the machine was suspended and has come back.
/// </summary>
public sealed class SystemResumedEventArgs : EventArgs
{
    public SystemResumedEventArgs(TimeSpan suspendedFor, SessionState state, bool sessionWouldHaveEnded)
    {
        SuspendedFor = suspendedFor;
        State = state;
        SessionWouldHaveEnded = sessionWouldHaveEnded;
    }

    /// <summary>Roughly how long the machine was asleep. Not charged to the session.</summary>
    public TimeSpan SuspendedFor { get; }

    public SessionState State { get; }

    /// <summary>
    /// True when the machine slept for longer than the session had left. The session is
    /// deliberately *not* completed — sleeping isn't focusing — but the user needs telling
    /// that the time they were away has not been credited.
    /// </summary>
    public bool SessionWouldHaveEnded { get; }
}

/// <summary>Raised shortly before a session ends, per TimerConfig.ReminderLeadTime.</summary>
public sealed class ReminderDueEventArgs : EventArgs
{
    public ReminderDueEventArgs(TimerMode mode, TimeSpan remaining, SessionState state)
    {
        Mode = mode;
        Remaining = remaining;
        State = state;
    }

    public TimerMode Mode { get; }
    public TimeSpan Remaining { get; }
    public SessionState State { get; }
}

public interface ITimerEngine
{
    SessionState CurrentState { get; }

    event EventHandler<TimerTickEventArgs>? Tick;
    event EventHandler<SessionEndedEventArgs>? SessionEnded;

    /// <summary>FR-101. Fires after the machine wakes from sleep.</summary>
    event EventHandler<SystemResumedEventArgs>? SystemResumed;

    /// <summary>Fires once per session, shortly before it ends.</summary>
    event EventHandler<ReminderDueEventArgs>? ReminderDue;

    /// <summary>Starts a fresh run at study session 1.</summary>
    void Start(TimerConfig config);

    /// <summary>FR-002. Starts a standalone break without running a focus session first.</summary>
    void StartBreak(TimerConfig config);

    /// <summary>Runs one focus session of the given length, then stops.</summary>
    void StartPredefined(TimerConfig config, TimeSpan duration);

    void Pause();
    void Resume();
    void Stop();

    /// <summary>Returns the current session to its full configured duration, paused.</summary>
    void Reset();

    /// <summary>FR-005. Ends the current session immediately and moves to the next one.</summary>
    void Skip();

    /// <summary>
    /// FR-013. Restores a run persisted from a previous launch. The timer is always
    /// resumed paused so a crash cannot silently burn session time.
    /// </summary>
    void Restore(TimerConfig config, SessionState state);
}
