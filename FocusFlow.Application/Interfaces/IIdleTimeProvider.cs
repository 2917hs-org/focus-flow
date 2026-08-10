namespace FocusFlow.Application.Interfaces;

/// <summary>
/// The platform-facing port for idle detection: how long it has been since the last
/// keyboard/mouse input, system-wide — not scoped to FocusFlow's own window, since the
/// point is to notice the user stepping away from the machine entirely. Same shape as
/// <see cref="IAppBlockingMonitor"/> — one real implementation per platform.
/// </summary>
public interface IIdleTimeProvider
{
    /// <summary>Time since the last input, or null if the platform can't report it.</summary>
    TimeSpan? GetIdleTime();
}
