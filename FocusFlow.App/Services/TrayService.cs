using System;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using FocusFlow.Application.Interfaces;

namespace FocusFlow.App.Services;

/// <summary>
/// Tray icon for every desktop platform, built on Avalonia's native <see cref="TrayIcon"/>
/// (Shell_NotifyIcon on Windows, NSStatusItem on macOS).
/// </summary>
/// <remarks>
/// Requests are surfaced as events rather than acted on directly. The popup shows the
/// ViewModel, and the ViewModel needs ITrayService to update the tooltip — holding the
/// window here would close that loop into a DI cycle. App wires the events up instead.
/// </remarks>
public sealed class TrayService : ITrayService, IDisposable
{
    private static readonly Uri IconUri = new("avares://FocusFlow.App/Assets/tray-icon.png");

    private readonly TrayIcon _trayIcon;

    public TrayService()
    {
        var open = new NativeMenuItem("Open FocusFlow");
        open.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) => Shutdown();

        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(new Bitmap(AssetLoader.Open(IconUri))),
            ToolTipText = "FocusFlow",
            IsVisible = true,
            Menu = new NativeMenu { open, exit },
        };

        // Left-click opens the compact popup; the full window sits behind "Open".
        _trayIcon.Clicked += (_, _) => PopupRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Tray icon clicked — show the compact popup.</summary>
    public event EventHandler? PopupRequested;

    /// <summary>"Open FocusFlow" chosen — show the full window.</summary>
    public event EventHandler? OpenRequested;

    public void UpdateTrayText(string text)
    {
        _trayIcon.ToolTipText = $"FocusFlow - {text}";
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
