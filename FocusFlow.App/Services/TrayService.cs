using System;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using FocusFlow.Application.Interfaces;

namespace FocusFlow.App.Services;

/// <summary>
/// Tray icon for every desktop platform, built on Avalonia's native <see cref="TrayIcon"/>
/// (Shell_NotifyIcon on Windows, NSStatusItem on macOS). Nothing here is OS-specific, so
/// it lives outside Platforms/ and compiles into both target frameworks.
/// </summary>
public sealed class TrayService : ITrayService, IDisposable
{
    private static readonly Uri IconUri = new("avares://FocusFlow.App/Assets/tray-icon.png");

    private readonly TrayIcon _trayIcon;
    private readonly IWindowPlacementService _placement;

    public TrayService(IWindowPlacementService placement)
    {
        _placement = placement;

        var open = new NativeMenuItem("Open FocusFlow");
        open.Click += (_, _) => ShowMainWindow();

        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) => Shutdown();

        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(new Bitmap(AssetLoader.Open(IconUri))),
            ToolTipText = "FocusFlow",
            IsVisible = true,
            Menu = new NativeMenu { open, exit },
        };

        _trayIcon.Clicked += (_, _) => ShowMainWindow();
    }

    public void UpdateTrayText(string text)
    {
        _trayIcon.ToolTipText = $"FocusFlow - {text}";
    }

    /// <summary>
    /// Closing the main window only hides it (see App.OnFrameworkInitializationCompleted),
    /// so this is the way back to a running instance.
    /// </summary>
    private void ShowMainWindow()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            // FR-106: reopen on whichever monitor the user is currently on, rather than
            // wherever the window happened to be hidden.
            _placement.ShowOnActiveScreen(window);
        }
    }

    /// <summary>
    /// ShutdownMode is OnExplicitShutdown, so this menu item is the only way to quit.
    /// </summary>
    private static void Shutdown()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    public void Dispose()
    {
        _trayIcon.IsVisible = false;
        _trayIcon.Dispose();
    }
}
