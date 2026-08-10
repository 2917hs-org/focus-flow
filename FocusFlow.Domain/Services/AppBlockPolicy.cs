using FocusFlow.Domain.Models;

namespace FocusFlow.Domain.Services;

/// <summary>
/// Whether a newly-frontmost app should trigger a block intervention.
/// </summary>
/// <remarks>
/// Pure function over already-known state, kept separate from anything that watches for
/// app switches: enforcement is only "the session is running and unpaused, and this app
/// is on the blocked list" — no native code needed to decide that.
/// </remarks>
public static class AppBlockPolicy
{
    public static bool ShouldIntervene(
        SessionState state, IReadOnlyCollection<string> blockedAppIds, string? activatedBundleId)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(blockedAppIds);

        return state.IsRunning
            && !string.IsNullOrWhiteSpace(activatedBundleId)
            && blockedAppIds.Contains(activatedBundleId, StringComparer.OrdinalIgnoreCase);
    }
}
