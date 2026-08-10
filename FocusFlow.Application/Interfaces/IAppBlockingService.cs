using FocusFlow.Domain.Models;

namespace FocusFlow.Application.Interfaces;

/// <summary>
/// The Settings-UI-facing surface of app blocking: whether it's available on this
/// platform/permission state, and what apps are running right now.
/// </summary>
public interface IAppBlockingService
{
    /// <summary>Same UX shape as IStartupService.IsSupported — false means "unsupported," not a silent no-op.</summary>
    bool IsSupported { get; }

    /// <summary>Every user-facing app installed on the machine, for the blocked-apps picker.</summary>
    IReadOnlyList<AppInfo> GetInstalledApplications();

    /// <summary>Opens System Settings to the Accessibility pane.</summary>
    void RequestAccessibilityAccess();

    /// <summary>The bundle id of whatever app is frontmost right now. Null if unsupported.</summary>
    string? GetFrontmostBundleId();

    /// <summary>Bundle id and display name of whatever app is frontmost right now. Null if unsupported.</summary>
    AppInfo? GetFrontmostApp();
}
