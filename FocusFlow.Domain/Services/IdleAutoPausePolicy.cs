using FocusFlow.Domain.Models;

namespace FocusFlow.Domain.Services;

/// <summary>
/// Whether an observed idle gap should pause the running session.
/// </summary>
/// <remarks>
/// Pure function over already-known state, same shape as <see cref="AppBlockPolicy"/>: no
/// native code needed to decide this, only whether a session is actually running
/// (unpaused) and the idle time has reached the configured threshold. Never suggests a
/// resume — that decision is left entirely to the user.
/// </remarks>
public static class IdleAutoPausePolicy
{
    public static bool ShouldPause(SessionState state, TimeSpan idleTime, TimeSpan threshold)
    {
        ArgumentNullException.ThrowIfNull(state);

        return state.IsRunning && threshold > TimeSpan.Zero && idleTime >= threshold;
    }
}
