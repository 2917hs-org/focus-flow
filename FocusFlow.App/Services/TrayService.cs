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
    private readonly IMenuBarCountdown _countdown;

    private readonly NativeMenuItem _statusItem;
    private readonly NativeMenuItem _breakItem;
    private readonly NativeMenuItem _predefinedItem;
    private readonly NativeMenuItem _pauseItem;
    private readonly NativeMenuItem _resumeItem;
    private readonly NativeMenuItem _skipItem;
    private readonly NativeMenuItem _resetItem;
    private readonly NativeMenuItem _stopItem;

    public TrayService(IMenuBarCountdown countdown)
    {
        _countdown = countdown;

        // Disabled on purpose: a readout, not an action.
        _statusItem = new NativeMenuItem("FocusFlow") { IsEnabled = false };

        // Prefixed with a plain Unicode glyph rather than NativeMenuItem.Icon (a Bitmap):
        // that needs a real per-action image asset. Picked from Geometric Shapes/Dingbats
        // rather than colour emoji, and ︎ (the "render as text, not emoji" variation
        // selector) is appended to every glyph capable of both — a thin monochrome sketch
        // like Safari's own menu icons, not a colourful pictograph.
        _breakItem = Action("☕︎  Take a Break", () => StartBreakRequested);
        _pauseItem = Action("⏸︎  Pause", () => PauseRequested);
        _resumeItem = Action("▶︎  Resume", () => ResumeRequested);
        _skipItem = Action("⏭︎  Skip", () => SkipRequested);
        _resetItem = Action("↺  Reset", () => ResetRequested);
        _stopItem = Action("⏹︎  Stop", () => StopRequested);

        // No bare "Start" item: it would start whatever length is currently saved in
        // settings, with no way to tell from the tray what that length actually is. The
        // labelled options below and "Open Main Window" (where the length is visible) are
        // the two ways to start without guessing.
        _predefinedItem = new NativeMenuItem("◎  Focus Session")
        {
            Menu = new NativeMenu
            {
                PredefinedAction("15 minutes", 15),
                PredefinedAction("30 minutes", 30),
                PredefinedAction("45 minutes", 45),
                PredefinedAction("60 minutes", 60)
            }
        };

        var open = new NativeMenuItem("⚙︎  Open Main Window");
        open.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

        var history = new NativeMenuItem("▤  View History");
        history.Click += (_, _) => ShowHistoryRequested?.Invoke(this, EventArgs.Empty);

        var quit = new NativeMenuItem("✕  Quit");
        quit.Click += (_, _) => Shutdown();

        var menu = new NativeMenu
        {
            _statusItem,
            new NativeMenuItemSeparator(),
            _predefinedItem,
            _breakItem,
            _pauseItem,
            _resumeItem,
            _skipItem,
            _resetItem,
            _stopItem
        };

        // macOS only, and built here rather than added-then-hidden: app blocking's
        // IAppBlockingMonitor is a no-op on Windows, so GetFrontmostApp() there would
        // always return null and the item would just sit in the menu doing nothing —
        // worse than not offering it. Quick-add rather than requiring the whole Manage
        // Blocked Apps window for "block the thing I'm looking at right now." Works from
        // the tray without stealing focus: a status-item menu doesn't activate its owning
        // app, so whatever's still frontmost when this is clicked is genuinely the app the
        // user was just in, not FocusFlow itself.
        if (OperatingSystem.IsMacOS())
        {
            var blockFrontmost = new NativeMenuItem("⊘  Block Frontmost App");
            blockFrontmost.Click += (_, _) => BlockFrontmostAppRequested?.Invoke(this, EventArgs.Empty);

            menu.Add(new NativeMenuItemSeparator());
            menu.Add(blockFrontmost);
        }

        menu.Add(new NativeMenuItemSeparator());
        menu.Add(open);
        menu.Add(history);
        menu.Add(quit);

        _staticIcon = new WindowIcon(new Bitmap(AssetLoader.Open(IdleIconUri)));

        _trayIcon = new TrayIcon
        {
            Icon = _staticIcon,
            ToolTipText = "FocusFlow",
            IsVisible = true,
            Menu = menu
        };

        // Windows raises this on left-click, and with the window hidden to the tray it is
        // the obvious way back to it — without a handler the icon simply ignored clicks.
        // macOS never raises it (the click goes to the menu), so this is a no-op there.
        _trayIcon.Clicked += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

        // Lets macOS invert the rendered countdown for a light or dark menu bar.
        MacOSProperties.SetIsTemplateIcon(_trayIcon, true);
    }

    public event EventHandler? StartBreakRequested;

    /// <summary>A fixed-length focus session was chosen; the argument is its length in minutes.</summary>
    public event EventHandler<int>? PredefinedRequested;
    public event EventHandler? PauseRequested;
    public event EventHandler? ResumeRequested;
    public event EventHandler? SkipRequested;
    public event EventHandler? ResetRequested;
    public event EventHandler? StopRequested;

    /// <summary>"Block Frontmost App" chosen — block whatever app was frontmost when clicked.</summary>
    public event EventHandler? BlockFrontmostAppRequested;

    /// <summary>"Open Main Window" chosen — show the full settings window.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>"View History" chosen — show the session history window.</summary>
    public event EventHandler? ShowHistoryRequested;

    public void UpdateStatus(TrayStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        _trayIcon.ToolTipText = $"FocusFlow — {status.Time}";
        _statusItem.Header = $"{status.Time}  ·  {status.Status}";

        // A no-op on Windows — see NoopMenuBarCountdown. On macOS this is the countdown
        // itself: real menu bar text, sized by AppKit, not a bitmap rendered into the icon.
        _countdown.SetVisible(status.CanStop);
        if (status.CanStop && status.IconLabel is not null)
        {
            _countdown.SetText(status.IconLabel);
        }

        // Hidden rather than greyed: a Pause entry that is permanently unavailable during
        // a study session would dangle the very option the design removes.
        _predefinedItem.IsVisible = status.CanStart;
        _breakItem.IsVisible = status.CanStartBreak;
        _pauseItem.IsVisible = status.CanPause;
        _resumeItem.IsVisible = status.CanResume;
        _skipItem.IsVisible = status.CanSkip;
        _resetItem.IsVisible = status.CanReset;
        _stopItem.IsVisible = status.CanStop;
    }

    private NativeMenuItem PredefinedAction(string header, int minutes)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => PredefinedRequested?.Invoke(this, minutes);
        return item;
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

        // _countdown is constructor-injected, not owned here — it's registered as its own
        // DI singleton, so the container disposes the real NSStatusItem when the
        // ServiceProvider itself is disposed. Disposing a dependency this class didn't
        // create would risk the container disposing it a second time.
    }
}
