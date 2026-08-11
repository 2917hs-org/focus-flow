using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace FocusFlow.App.Views;

public partial class HistoryWindow : Window
{
    /// <summary>
    /// "‹ Back" clicked, or Escape pressed — same action either way. App.axaml.cs hides
    /// this window and brings MainWindow forward in response, keeping that orchestration
    /// out of the Window class itself, same as every other cross-window concern in this
    /// app (Closing, tray, hotkeys). Named NavigateBackRequested, not BackRequested: the
    /// latter already exists on TopLevel (Android's hardware back button) and would just
    /// be silently hidden rather than actually overridden.
    /// </summary>
    public event EventHandler? NavigateBackRequested;

    public HistoryWindow()
    {
        InitializeComponent();

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                NavigateBackRequested?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    private void OnBackClick(object? sender, RoutedEventArgs e) =>
        NavigateBackRequested?.Invoke(this, EventArgs.Empty);
}
