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
/// <para>
/// Everything lives on the native menu. Two attempts to open a window on click failed on
/// macOS: with a menu attached the click goes to the menu, and with no menu attached
/// Avalonia's macOS backend does not raise Clicked at all, which left the icon inert. The
/// menu is the only surface the platform reliably gives us, so it carries the readout, the
/// transport controls and both navigation actions.
/// </para>
/// <para>
/// Actions are surfaced as events rather than acted on directly. The tray shows the
/// ViewModel's state and the ViewModel needs ITrayService to push it — holding the
/// ViewModel here would close that loop into a DI cycle. App wires the events up instead.
/// </para>
/// </remarks>
public sealed class TrayService : ITrayService, IDisposable
{
    /// <summary>
    /// macOS gets a monochrome template cut, Windows the full-colour logo.
    /// </summary>
    /// <remarks>
    /// A template image is an alpha mask — macOS discards the colours and paints whatever
    /// is opaque, inverting it for the menu bar's appearance. The colour logo has an
    /// opaque body *and* an opaque brain, so under that treatment it collapses into one
    /// featureless silhouette. The template cut knocks the brain out to negative space so
    /// the mark still reads. Windows tray icons render in colour, so they use the original.
    /// </remarks>
    /// <remarks>
    /// The assembly name is read rather than written out. An avares URI embeds it, so
    /// hardcoding "FocusFlow.App" silently broke every icon the moment AssemblyName was
    /// set to "FocusFlow" for packaging — and it broke at runtime, not at build time.
    /// </remarks>
    private static readonly Uri IdleIconUri = new(
        $"avares://{typeof(TrayService).Assembly.GetName().Name}/Assets/"
        + (OperatingSystem.IsMacOS() ? "tray-template.png" : "app-icon.png"));

    private readonly TrayIcon _trayIcon;
    private readonly WindowIcon _staticIcon;

    /// <summary>Bitmap currently displayed as the icon, kept so it can be released.</summary>
    private RenderTargetBitmap? _renderedIcon;

    /// <summary>Text the current icon shows, so it is only redrawn when the second flips.</summary>
    private string? _renderedText;

    private readonly NativeMenuItem _statusItem;
    private readonly NativeMenuItem _startItem;
    private readonly NativeMenuItem _breakItem;
    private readonly NativeMenuItem _pauseItem;
    private readonly NativeMenuItem _resumeItem;
    private readonly NativeMenuItem _skipItem;
    private readonly NativeMenuItem _resetItem;
    private readonly NativeMenuItem _stopItem;

    public TrayService()
    {
        // Disabled on purpose: a readout, not an action.
        _statusItem = new NativeMenuItem("FocusFlow") { IsEnabled = false };

        _startItem = Action("Start", () => StartRequested);
        _breakItem = Action("Take a Break", () => StartBreakRequested);
        _pauseItem = Action("Pause", () => PauseRequested);
        _resumeItem = Action("Resume", () => ResumeRequested);
        _skipItem = Action("Skip", () => SkipRequested);
        _resetItem = Action("Reset", () => ResetRequested);
        _stopItem = Action("Stop", () => StopRequested);

        var open = new NativeMenuItem("Open Main Window");
        open.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => Shutdown();

        _staticIcon = new WindowIcon(new Bitmap(AssetLoader.Open(IdleIconUri)));

        _trayIcon = new TrayIcon
        {
            Icon = _staticIcon,
            ToolTipText = "FocusFlow",
            IsVisible = true,
            Menu = new NativeMenu
            {
                _statusItem,
                new NativeMenuItemSeparator(),
                _startItem,
                _breakItem,
                _pauseItem,
                _resumeItem,
                _skipItem,
                _resetItem,
                _stopItem,
                new NativeMenuItemSeparator(),
                open,
                quit
            }
        };

        // Lets macOS invert the rendered countdown for a light or dark menu bar.
        MacOSProperties.SetIsTemplateIcon(_trayIcon, true);
    }

    public event EventHandler? StartRequested;
    public event EventHandler? StartBreakRequested;
    public event EventHandler? PauseRequested;
    public event EventHandler? ResumeRequested;
    public event EventHandler? SkipRequested;
    public event EventHandler? ResetRequested;
    public event EventHandler? StopRequested;

    /// <summary>"Open Main Window" chosen — show the full settings window.</summary>
    public event EventHandler? OpenRequested;

    public void UpdateStatus(TrayStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        _trayIcon.ToolTipText = $"FocusFlow — {status.Time}";
        _statusItem.Header = $"{status.Time}  ·  {status.Status}";

        UpdateIcon(status);

        // Hidden rather than greyed: a Pause entry that is permanently unavailable during
        // a study session would dangle the very option the design removes.
        _startItem.IsVisible = status.CanStart;
        _breakItem.IsVisible = status.CanStartBreak;
        _pauseItem.IsVisible = status.CanPause;
        _resumeItem.IsVisible = status.CanResume;
        _skipItem.IsVisible = status.CanSkip;
        _resetItem.IsVisible = status.CanReset;
        _stopItem.IsVisible = status.CanStop;
    }

    /// <summary>
    /// Shows the countdown as the icon itself while a session runs, falling back to the
    /// logo when idle.
    /// </summary>
    /// <remarks>
    /// macOS only. A Windows tray icon is a small fixed square, so a wide text bitmap would
    /// be squashed into something unreadable; there the tooltip carries the time.
    /// </remarks>
    private void UpdateIcon(TrayStatus status)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var text = status.CanStop ? status.IconLabel : null;

        if (text == _renderedText)
        {
            return;
        }

        _renderedText = text;
        var previous = _renderedIcon;

        if (text is null)
        {
            _renderedIcon = null;
            _trayIcon.Icon = _staticIcon;
        }
        else
        {
            _renderedIcon = TrayIconRenderer.Render(text);
            _trayIcon.Icon = new WindowIcon(_renderedIcon);
        }

        // Released only after the replacement is in place — the tray still holds a
        // reference to the old bitmap until then.
        previous?.Dispose();
    }

    /// <summary>
    /// Builds a menu item whose handler resolves the event lazily, so items can be created
    /// before the events have subscribers.
    /// </summary>
    private NativeMenuItem Action(string header, Func<EventHandler?> handler)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => handler()?.Invoke(this, EventArgs.Empty);
        return item;
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
        _renderedIcon?.Dispose();
    }
}
