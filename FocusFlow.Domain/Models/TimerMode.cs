namespace FocusFlow.Domain.Models;

/// <summary>
/// What the timer is currently counting. Pausing is tracked separately by
/// <see cref="SessionState.IsPaused"/> so a paused timer still remembers whether it was
/// in a study or a break session — the old Paused member here made that impossible.
/// </summary>
public enum TimerMode
{
    Idle,
    Study,
    Break
}
