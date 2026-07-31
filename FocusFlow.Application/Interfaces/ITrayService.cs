namespace FocusFlow.Application.Interfaces;

public interface ITrayService
{
    /// <summary>
    /// Updates the tray tooltip, e.g. with the remaining time.
    /// </summary>
    /// <remarks>
    /// There is deliberately no ShowContextMenu here: with a native tray icon the OS
    /// owns menu display (right-click on Windows, click on macOS), and neither platform
    /// exposes a way to open it programmatically. Nothing called it.
    /// </remarks>
    void UpdateTrayText(string text);
}