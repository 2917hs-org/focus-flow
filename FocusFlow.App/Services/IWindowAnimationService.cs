using Avalonia.Controls;

namespace FocusFlow.App.Services;

/// <summary>
/// Suppresses the platform's own default show/hide animation for a window this app owns —
/// macOS fades/scales any newly-shown NSWindow in by default, which read as an unwanted
/// flourish once MainWindow/HistoryWindow/BlockedAppsWindow all started opening in the
/// exact same spot every time; a window snapping straight into place there reads as one
/// continuous surface, a window fading in each time reads as three separate windows again.
/// </summary>
public interface IWindowAnimationService
{
    /// <summary>
    /// Call once the window's native handle actually exists — Window.Opened, not the
    /// constructor — and only once per window: the setting lives on the native window
    /// itself, which App.axaml.cs's Hide()/Show() cycle reuses rather than recreating, so
    /// it persists across every later appearance without needing to be reapplied.
    /// </summary>
    void DisableShowHideAnimation(Window window);
}
