using System;
using FocusFlow.Application.Interfaces;
using Microsoft.Toolkit.Uwp.Notifications;

namespace FocusFlow.App.Platforms.Windows;

/// <summary>
/// FR-008 on Windows, via Action Center toasts — the counterpart to
/// Platforms/MacOS/MacNotificationService. Compiled only into the -windows target
/// framework, which is what makes ToastNotificationManagerCompat available.
/// </summary>
/// <remarks>
/// An unpackaged Win32 app calling this API without a registered AppUserModelID is a
/// well-known way to get a COMException here — and unlike the macOS counterpart (which
/// shells out to osascript and was already guarded), this call had no error handling at
/// all. A failed notification must never take the timer down with it, same as every other
/// best-effort platform call in this app.
/// </remarks>
public sealed class WindowsNotificationService : INotificationService
{
    private readonly IAppLogger? _logger;

    public WindowsNotificationService(IAppLogger? logger = null)
    {
        _logger = logger;

        try
        {
            ToastNotificationManagerCompat.OnActivated += toastArgs =>
            {
                // Handle notification activation if needed
            };
        }
        catch (Exception e)
        {
            _logger?.Warn($"Registering toast activation failed: {e.Message}");
        }
    }

    public void ShowNotification(string title, string message)
    {
        try
        {
            new ToastContentBuilder()
                .AddText(title)
                .AddText(message)
                .Show(toast =>
                {
                    toast.Tag = "FocusFlow";
                    toast.Group = "FocusFlow";
                });
        }
        catch (Exception e)
        {
            // A failed notification must never take the timer down with it.
            _logger?.Warn($"Showing a toast notification failed: {e.Message}");
        }
    }
}
