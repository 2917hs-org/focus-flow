using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace FocusFlow.App.Views;

/// <summary>
/// The always-on-top widget shown while a session is running — a small floating readout
/// that stays above every other window, the way the macOS Stopwatch's own Live Activity
/// floats above the desktop. App shows and hides it as the session starts and ends; it is
/// never closed by the user, only dragged.
/// </summary>
public partial class MiniTimerWindow : Window
{
    /// <summary>
    /// Placed once, the first time the widget is ever shown. After that its position is
    /// whatever the user last dragged it to — Show()/Hide() toggles visibility on the same
    /// instance rather than recreating the window, so nothing needs to re-place it.
    /// </summary>
    private bool _positioned;

    public MiniTimerWindow()
    {
        InitializeComponent();
        Opened += OnOpened;

        // No title bar means no OS close control, but belt-and-braces: if anything ever
        // asks this window to close outright, hide instead so App's App-level toggle
        // remains the only thing that owns its lifetime.
        Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (_positioned)
        {
            return;
        }

        _positioned = true;
        PositionTopRight();
    }

    /// <summary>Default spot: top-right of whichever screen the widget opened on.</summary>
    private void PositionTopRight()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null)
        {
            return;
        }

        var area = screen.WorkingArea;
        var margin = (int)(16 * screen.Scaling);
        var width = (int)(Bounds.Width * screen.Scaling);

        Position = new PixelPoint(
            area.X + area.Width - width - margin,
            area.Y + margin);
    }

    /// <summary>
    /// Only the time/status area triggers a drag — the transport row below needs its own
    /// clicks to reach the buttons.
    /// </summary>
    private void OnDragHandlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
