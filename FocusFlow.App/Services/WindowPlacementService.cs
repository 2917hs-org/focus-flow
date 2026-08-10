using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace FocusFlow.App.Services;

/// <summary>
/// FR-106. Places the window on the monitor the user is actually looking at, and keeps it
/// from stranding off-screen when the display layout changes.
/// </summary>
public interface IWindowPlacementService
{
    /// <summary>Shows and activates <paramref name="window"/> on the active monitor.</summary>
    void ShowOnActiveScreen(Window window);
}

public sealed class WindowPlacementService : IWindowPlacementService
{
    private readonly IPointerLocator _pointerLocator;

    public WindowPlacementService(IPointerLocator pointerLocator)
    {
        _pointerLocator = pointerLocator;
    }

    public void ShowOnActiveScreen(Window window)
    {
        // Only place it when it is actually being brought back from hidden or minimised.
        // If the window is already on screen the user has put it where they want it, and
        // yanking it to the middle of another display because that is where the pointer
        // happens to be is disorienting — they asked to be shown the window, not to have
        // it moved.
        var isReturning = !window.IsVisible || window.WindowState == WindowState.Minimized;

        if (isReturning && ResolveTargetScreen(window) is { } screen)
        {
            // WorkingArea, not Bounds: respects the taskbar/Dock/menu bar so the window
            // isn't centred under them.
            var area = screen.WorkingArea;

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

    private Screen? ResolveTargetScreen(Window window)
    {
        var screens = window.Screens;
        if (screens is null || screens.ScreenCount == 0)
        {
            return null;
        }

        // Preferred: the screen the pointer is on — the best available proxy for "the
        // monitor the user is working on".
        if (_pointerLocator.TryGetPosition(out var pointer))
        {
            var atPointer = screens.ScreenFromPoint(pointer);
            if (atPointer is not null)
            {
                return atPointer;
            }
        }

        // Otherwise stay where the window already is, but only if that screen is still
        // connected — a window last shown on an unplugged monitor would otherwise be
        // restored somewhere the user cannot reach it.
        var current = screens.ScreenFromWindow(window);
        if (current is not null && screens.All.Contains(current))
        {
            return current;
        }

        return screens.Primary ?? (screens.All.Count > 0 ? screens.All[0] : null);
    }
}
