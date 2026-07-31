using FocusFlow.Application.Interfaces;
using Microsoft.Toolkit.Uwp.Notifications;

namespace FocusFlow.App.Platforms.Windows;

/// <summary>
/// FR-008 on Windows, via Action Center toasts — the counterpart to
/// Platforms/MacOS/MacNotificationService. Compiled only into the -windows target
/// framework, which is what makes ToastNotificationManagerCompat available.
/// </summary>
public sealed class WindowsNotificationService : INotificationService
{
    public WindowsNotificationService()
    {
        ToastNotificationManagerCompat.OnActivated += toastArgs =>
        {
            // Handle notification activation if needed
        };
    }

    public void ShowNotification(string title, string message)
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
}
