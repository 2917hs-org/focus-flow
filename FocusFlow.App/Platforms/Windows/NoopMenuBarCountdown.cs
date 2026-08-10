using FocusFlow.Application.Interfaces;

namespace FocusFlow.App.Platforms.Windows;

/// <summary>
/// Windows has no equivalent of a native status-item text title — Shell_NotifyIcon carries
/// only an icon and a tooltip, and the tooltip already shows the time. Nothing to do here.
/// </summary>
public sealed class NoopMenuBarCountdown : IMenuBarCountdown
{
    public void SetVisible(bool visible)
    {
    }

    public void SetText(string text)
    {
    }
}
