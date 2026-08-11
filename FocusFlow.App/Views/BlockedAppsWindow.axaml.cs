using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using FocusFlow.App.ViewModels;

namespace FocusFlow.App.Views;

public partial class BlockedAppsWindow : Window
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

    public BlockedAppsWindow()
    {
        InitializeComponent();

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                NavigateBackRequested?.Invoke(this, EventArgs.Empty);
            }
        };

        UnblockAllButton.Click += OnUnblockAllClick;
    }

    private void OnBackClick(object? sender, RoutedEventArgs e) =>
        NavigateBackRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Unblock (one selection) doesn't ask first — the user already made a deliberate
    /// choice by checking boxes. Unblock all is one click away from clearing the whole
    /// list with no undo, which is exactly the kind of bulk-destructive action that
    /// deserves a confirmation, per this app's own AlertWindow already supporting the
    /// two-button confirm/decline form for opt-in actions.
    /// </summary>
    private async void OnUnblockAllClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not BlockedAppsViewModel viewModel || viewModel.BlockedApps.Count == 0)
        {
            return;
        }

        var count = viewModel.BlockedApps.Count;
        var confirmed = await new AlertWindow(
            "Unblock all apps?",
            $"This removes all {count} app{(count == 1 ? "" : "s")} from the blocked list. "
            + "You can add them again later.",
            confirmLabel: "Unblock All",
            declineLabel: "Cancel").ShowDialog<bool>(this);

        if (confirmed && viewModel.RemoveAllBlockedAppsCommand.CanExecute(null))
        {
            viewModel.RemoveAllBlockedAppsCommand.Execute(null);
        }
    }
}
