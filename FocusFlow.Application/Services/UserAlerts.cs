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
    private readonly IAppLogger? _logger;

    // Optional and defaulted, like every other cross-cutting collaborator in this layer
    // (see IUserAlerts? on the services that report through here) — tests construct this
    // with no logger and don't need to care that one exists.
    public UserAlerts(IAppLogger? logger = null)
    {
        _logger = logger;
    }

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

        // The dialog the user sees is transient; this is what's left once they dismiss
        // it, in case the same problem is still worth explaining next time they ask.
        _logger?.Error($"{heading} — {body}");
        AlertRaised?.Invoke(this, (heading, body));
    }
}
