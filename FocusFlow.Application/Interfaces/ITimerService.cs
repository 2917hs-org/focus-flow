using FocusFlow.Domain.Interfaces;
using FocusFlow.Domain.Models;

namespace FocusFlow.Application.Interfaces;

public class TimerUpdatedEventArgs : EventArgs
{
    public TimerUpdatedEventArgs(SessionState state)
    {
        State = state;
    }

    public SessionState State { get; }
}

public interface ITimerService
{
    SessionState CurrentState { get; }

    /// <summary>Fires once per second while a session runs, and on every state change.</summary>
    event EventHandler<TimerUpdatedEventArgs>? TimerUpdated;

    /// <summary>
    /// Fires when a session reaches zero. Consumers should alert from this rather than
    /// watching for RemainingTime == 0 on <see cref="TimerUpdated"/>, which cannot tell
    /// "just finished" apart from "sitting at zero waiting to be started".
    /// </summary>
    event EventHandler<SessionEndedEventArgs>? SessionEnded;

    /// <summary>FR-101. Fires after the machine wakes from sleep.</summary>
    event EventHandler<SystemResumedEventArgs>? SystemResumed;

    /// <summary>Fires shortly before a session ends.</summary>
    event EventHandler<ReminderDueEventArgs>? ReminderDue;

    Task StartAsync(TimerConfig config);

    /// <summary>FR-002. Runs a single break with no study session attached.</summary>
    Task StartBreakAsync(TimerConfig config);

    void Pause();
    void Resume();
    void Stop();
    void Reset();

    /// <summary>FR-005. Ends the current session and starts the next immediately.</summary>
    void Skip();

    /// <summary>FR-013. Reinstates a run saved by a previous launch, paused.</summary>
    void Restore(TimerConfig config, SessionState state);
}
