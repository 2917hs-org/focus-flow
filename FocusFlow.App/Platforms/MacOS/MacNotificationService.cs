using System;
using System.Diagnostics;
using FocusFlow.Application.Interfaces;

namespace FocusFlow.App.Platforms.MacOS;

/// <summary>
/// FR-008 on macOS, via osascript — the counterpart to the Action Center toasts in
/// Platforms/Windows/WindowsNotificationService.
/// </summary>
/// <remarks>
/// Compiled into the non-Windows (net10.0) target framework, which also covers Linux, so
/// the call is guarded by an OS check and degrades to a no-op there.
/// </remarks>
public sealed class MacNotificationService : INotificationService
{
    public void ShowNotification(string title, string message)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo("osascript")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add(
                $"display notification \"{Escape(message)}\" with title \"{Escape(title)}\"");

            using var process = Process.Start(startInfo);
        }
        catch (Exception)
        {
            // A failed notification must never take the timer down with it.
        }
    }

    /// <summary>
    /// Escapes an AppleScript string literal. ArgumentList already handles OS-level
    /// argument quoting, so only the literal's own backslashes and quotes matter.
    /// </summary>
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
