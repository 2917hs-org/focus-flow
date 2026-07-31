namespace FocusFlow.Application.Interfaces;

/// <summary>
/// FR-012. Registers the app to launch when the user logs in.
/// </summary>
/// <remarks>
/// Both implementations are per-user (HKCU on Windows, ~/Library/LaunchAgents on macOS),
/// so this never needs administrator rights.
/// </remarks>
public interface IStartupService
{
    /// <summary>False when this build cannot register itself, e.g. an unsupported OS.</summary>
    bool IsSupported { get; }

    bool IsEnabled();

    /// <summary>Returns true when the change was applied.</summary>
    bool SetEnabled(bool enabled);
}
