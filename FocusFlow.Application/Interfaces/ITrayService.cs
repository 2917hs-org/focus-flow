namespace FocusFlow.Application.Interfaces;

/// <summary>
/// Everything the tray needs to render itself: the countdown, a one-line status, and which
/// actions currently apply.
/// </summary>
/// <remarks>
/// <para>
/// The availability flags travel with the status rather than being re-derived in the tray,
/// so the menu can never disagree with the window about whether, say, Pause applies.
/// </para>
/// <para>
/// <c>IconLabel</c> is drawn straight into the menu bar icon, ticking every second like
/// the macOS Stopwatch's own menu bar readout — the same mm:ss text as <c>Time</c>, which
/// also carries the tooltip and the menu readout. Null when nothing is running.
/// </para>
/// </remarks>
public sealed record TrayStatus(
    string Time,
    string? IconLabel,
    string Status,
    bool CanStart,
    bool CanStartBreak,
    bool CanPause,
    bool CanResume,
    bool CanSkip,
    bool CanReset,
    bool CanStop);

public interface ITrayService
{
    /// <summary>
    /// Pushes the current countdown and available actions into the tray icon and its menu.
    /// </summary>
    /// <remarks>
    /// There is deliberately no ShowContextMenu here: with a native tray icon the OS owns
    /// menu display (right-click on Windows, click on macOS), and neither platform exposes
    /// a way to open it programmatically.
    /// </remarks>
    void UpdateStatus(TrayStatus status);
}
