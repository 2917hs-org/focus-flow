namespace FocusFlow.Application.Interfaces;

/// <summary>
/// Surfaces problems the user has to act on — chiefly a data folder FocusFlow cannot write
/// to.
/// </summary>
/// <remarks>
/// These failures were previously caught and dropped, on the reasoning that a failed write
/// should never interrupt a running session. That part still holds; what was wrong was
/// saying nothing at all, so settings would silently stop persisting with no clue why.
/// </remarks>
public interface IUserAlerts
{
    /// <summary>
    /// Reports a problem once. Repeats of the same <paramref name="key"/> are dropped —
    /// the session snapshot retries every few seconds, so an unwritable disk would
    /// otherwise raise an alert on a loop.
    /// </summary>
    void Report(string key, string heading, string body);
}
