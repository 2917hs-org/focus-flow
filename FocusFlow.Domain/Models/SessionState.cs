namespace FocusFlow.Domain.Models;

/// <summary>
/// An immutable snapshot of the timer at one instant.
/// </summary>
/// <remarks>
/// The engine counts down on a background timer while the UI reads on the dispatcher
/// thread. Handing out snapshots rather than a shared mutable object is what keeps the
/// UI from observing half-updated state.
/// </remarks>
public sealed record SessionState
{
    public TimerMode Mode { get; init; } = TimerMode.Idle;

    /// <summary>Time left in the current study or break session.</summary>
    public TimeSpan RemainingTime { get; init; }

    /// <summary>1-based count of study sessions started since the timer was last stopped.</summary>
    public int CurrentSession { get; init; } = 1;

    public bool IsPaused { get; init; }

    public DateTimeOffset? LastUpdateTime { get; init; }

    /// <summary>True while a session is counting down — i.e. started and not paused.</summary>
    public bool IsRunning => Mode != TimerMode.Idle && !IsPaused;
}
