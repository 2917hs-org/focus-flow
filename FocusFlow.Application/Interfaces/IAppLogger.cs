namespace FocusFlow.Application.Interfaces;

/// <summary>
/// A single, minimal sink for anything worth finding after the fact. There is no way to
/// attach a debugger to a user's machine, so this is the only record of what the app did
/// when something went wrong.
/// </summary>
/// <remarks>
/// Deliberately three methods, not a generic log-level framework: nothing here needs
/// structured logging, multiple sinks or filtering, and adding that machinery would be
/// solving a problem this app doesn't have.
/// </remarks>
public interface IAppLogger
{
    void Info(string message);

    void Warn(string message);

    void Error(string message, Exception? exception = null);
}
