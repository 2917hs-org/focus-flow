using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace FocusFlow.App.Services;

/// <summary>
/// FR-106. Places every window (MainWindow, HistoryWindow, BlockedAppsWindow) centred on
/// the primary display, so switching between them never means the next one landing
/// somewhere different. Also keeps a window from stranding off-screen when the display
/// layout changes.
/// </summary>
public interface IWindowPlacementService
{
    /// <summary>Shows and activates <paramref name="window"/>, centred on the primary display.</summary>
    void ShowCentered(Window window);
}

public sealed class WindowPlacementService : IWindowPlacementService
{
    public void ShowCentered(Window window)
    {
        // Only place it when it is actually being brought back from hidden or minimised.
        // If the window is already on screen the user has put it where they want it, and
        // yanking it back to centre because it was asked for again is disorienting — they
        // asked to be shown the window, not to have it moved.
        var isReturning = !window.IsVisible || window.WindowState == WindowState.Minimized;

        if (isReturning && ResolveTargetScreen(window) is { } screen)
        {
            // WorkingArea, not Bounds: respects the taskbar/Dock/menu bar so the window
            // isn't centred underneath them.
            var area = screen.WorkingArea;

            // A window sized (or SizeToContent-grown, historically) for a tall display can
            // exceed a smaller one's working area entirely, leaving part of it permanently
            // unreachable since nothing here lets the user drag it back into view. Clamping
            // before centring keeps the whole window on screen; Window.MinWidth/MinHeight
            // still apply, so this can't shrink a window past what its own content needs.
            var maxWidth = area.Width / screen.Scaling;
            var maxHeight = area.Height / screen.Scaling;

            if (window.Width > maxWidth)
            {
                window.Width = maxWidth;
            }

            if (window.Height > maxHeight)
            {
                window.Height = maxHeight;
            }

            // Bounds are physical pixels; the window is sized in device-independent
            // units, so it has to be scaled up before centring or the window lands
            // off-centre on any HiDPI display (FR-105 + FR-106 together).
            var width = (int)(window.Width * screen.Scaling);
            var height = (int)(window.Height * screen.Scaling);

            window.Position = new PixelPoint(
                area.X + ((area.Width - width) / 2),
                area.Y + ((area.Height - height) / 2));
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();

        // Raise and focus. Show() alone leaves an already-visible window exactly where it
        // was in the stacking order, so choosing "Open Main Window" while it sat behind
        // the editor would appear to do nothing at all.
        window.Activate();
        window.Focus();
    }

    /// <summary>
    /// Always the primary display, not wherever the pointer or a previous window happens
    /// to be — with an external monitor connected, either of those could easily point at
    /// it instead, and a window that opens on a different screen depending on where the
    /// mouse was last is exactly the inconsistency this exists to avoid.
    /// </summary>
    private static Screen? ResolveTargetScreen(Window window)
    {
        var screens = window.Screens;
        if (screens is null || screens.ScreenCount == 0)
        {
            return null;
        }

        return screens.Primary ?? (screens.All.Count > 0 ? screens.All[0] : null);
    }
}
