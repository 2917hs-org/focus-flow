using System;
using Avalonia.Controls;

namespace FocusFlow.App.Views;

/// <summary>
/// The compact panel shown when the tray icon is clicked: current status, progress and the
/// controls worth reaching for mid-session. "Open" escalates to the full window.
/// </summary>
public partial class TrayPopup : Window
{
    public TrayPopup()
    {
        InitializeComponent();

        OpenButton.Click += (_, _) =>
        {
            Hide();
            OpenRequested?.Invoke(this, EventArgs.Empty);
        };

        ExitButton.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        // Behaves like a menu: clicking away dismisses it rather than leaving a stray
        // always-on-top window behind.
        Deactivated += (_, _) => Hide();
    }

    public event EventHandler? OpenRequested;
    public event EventHandler? ExitRequested;
}
