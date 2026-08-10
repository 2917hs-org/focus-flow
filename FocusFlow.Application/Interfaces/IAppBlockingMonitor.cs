using FocusFlow.Domain.Models;

namespace FocusFlow.Application.Interfaces;

/// <summary>
/// The platform-facing port for app blocking: watching for a foreground-app switch and
/// acting on it. Same shape as <see cref="IMenuBarCountdown"/> — one real implementation
/// on macOS, a no-op on Windows.
/// </summary>
public interface IAppBlockingMonitor : IDisposable
{
    /// <summary>
    /// True only on macOS with Accessibility access already granted
    /// (AXIsProcessTrusted). False everywhere else, including "could work but the user
    /// hasn't granted access yet" — same UX shape as IStartupService.IsSupported.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>Fires with the bundle id of the app that just became frontmost.</summary>
    event EventHandler<string>? AppActivated;

    /// <summary>
    /// Every user-facing app installed on the machine (not just currently-running ones),
    /// for the blocked-apps picker — so an app can be blocked before it's ever launched.
    /// </summary>
    IReadOnlyList<AppInfo> GetInstalledApplications();

    /// <summary>Opens System Settings to the Accessibility pane.</summary>
    void RequestAccessibilityAccess();

    /// <summary>
    /// The bundle id of whatever app is frontmost right now, on demand — not the polled
    /// value StartWatching/AppActivated track, which only run during an active session.
    /// Null if unsupported or nothing could be read.
    /// </summary>
    string? GetFrontmostBundleId();

    /// <summary>
    /// Bundle id and display name of whatever app is frontmost right now — the tray menu's
    /// "Block Frontmost App" quick-add needs the name too, not just the id
    /// GetFrontmostBundleId returns. Null under the same conditions as GetFrontmostBundleId.
    /// </summary>
    AppInfo? GetFrontmostApp();

    /// <summary>Begins watching for foreground-app switches. A no-op if already watching.</summary>
    void StartWatching();

    /// <summary>Stops watching. A no-op if not currently watching.</summary>
    void StopWatching();

    /// <summary>
    /// Hides the given app and brings FocusFlow forward. Never terminates the app.
    /// </summary>
    void Intervene(string bundleId);
}
