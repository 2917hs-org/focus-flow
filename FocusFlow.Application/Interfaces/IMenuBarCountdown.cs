namespace FocusFlow.Application.Interfaces;

/// <summary>
/// A native, OS-drawn text readout beside the tray icon — the countdown itself, separate
/// from the menu <see cref="ITrayService"/> builds.
/// </summary>
/// <remarks>
/// Its own interface rather than a method on <see cref="ITrayService"/> because only macOS
/// has anywhere to put it: NSStatusItem can carry a text title that AppKit sizes correctly
/// on its own, which is exactly the API Windows' Shell_NotifyIcon has no equivalent for —
/// there the tooltip already carries the time. See NativeMenuBarCountdown's remarks for why
/// this replaced rendering the countdown into the tray icon's bitmap.
/// </remarks>
public interface IMenuBarCountdown
{
    /// <summary>Shows or hides the readout. Hidden means no menu-bar footprint at all.</summary>
    void SetVisible(bool visible);

    /// <summary>Sets the displayed text. A no-op if it hasn't changed since the last call.</summary>
    void SetText(string text);
}
