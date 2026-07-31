using FocusFlow.Application.Interfaces;

namespace FocusFlow.Application.Services;

/// <summary>
/// Collects problem reports and re-raises them for whoever owns the UI.
/// </summary>
/// <remarks>
/// Lives in the application layer rather than beside the window it ends up in: the
/// once-only rule is behaviour worth testing, and nothing here touches a UI type.
/// </summary>
public sealed class UserAlerts : IUserAlerts
{
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public event EventHandler<(string Heading, string Body)>? AlertRaised;

    public void Report(string key, string heading, string body)
    {
        lock (_gate)
        {
            // First occurrence only. A read-only data folder fails on every save attempt,
            // and a dialog every five seconds would be worse than the original problem.
            if (!_reported.Add(key))
            {
                return;
            }
        }

        AlertRaised?.Invoke(this, (heading, body));
    }
}
