using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using FocusFlow.App.Services;
using FocusFlow.App.ViewModels;
using FocusFlow.App.Views;
using Avalonia.Threading;
using FocusFlow.Application.Interfaces;
using FocusFlow.Application.Services;
using FocusFlow.Domain.Engines;
using FocusFlow.Domain.Interfaces;
using FocusFlow.Infrastructure.Logging;
using FocusFlow.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
#if WINDOWS
using FocusFlow.App.Platforms.Windows;
#else
using FocusFlow.App.Platforms.MacOS;
#endif

namespace FocusFlow.App;

public partial class App : Avalonia.Application
{
    private ServiceProvider? _provider;
    private MainWindow? _mainWindow;
    private MiniTimerWindow? _miniTimerWindow;
    private HistoryWindow? _historyWindow;
    private BlockedAppsWindow? _blockedAppsWindow;
    private IWindowPlacementService? _placement;

    /// <summary>Set by Program when another launch was detected and refused.</summary>
    public static SingleInstanceGuard? InstanceGuard { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // Each platform gets its own full Light/Dark palette — merged here rather than
        // declared as static resources in App.axaml, because a plain <Color x:Key="…">
        // there would apply to every platform unconditionally regardless of which one is
        // actually running. Both dictionaries declare Light/Dark ThemeDictionaries, so
        // DynamicResource lookups everywhere else re-resolve automatically when
        // ActualThemeVariant flips — nothing downstream needs to know which platform it's
        // on.
        Resources.MergedDictionaries.Add(
            OperatingSystem.IsMacOS() ? new Themes.MacOSPalette() : new Themes.WindowsPalette());

        if (OperatingSystem.IsMacOS())
        {
            ApplyMacOSStyling();
        }
    }

    /// <summary>
    /// Rounds the stock input controls' corners closer to macOS's than Fluent's default
    /// 4px. The semantic success/warning/danger/info buttons in App.axaml set their own
    /// look and are untouched by this.
    /// </summary>
    private void ApplyMacOSStyling()
    {
        Styles.Add(RoundedCorners<Button>());
        Styles.Add(RoundedCorners<ComboBox>());
        Styles.Add(RoundedCorners<TextBox>());
        Styles.Add(RoundedCorners<NumericUpDown>());
    }

    /// <summary>
    /// Looked up through the property registry rather than a hardcoded
    /// "<typeparamref name="T"/>.CornerRadiusProperty" reference, since which class in
    /// the hierarchy actually declares it isn't this method's business to know.
    /// </summary>
    private static Style RoundedCorners<T>() where T : Control
    {
        var property = AvaloniaPropertyRegistry.Instance.FindRegistered(typeof(T), "CornerRadius")
            ?? throw new InvalidOperationException($"{typeof(T).Name} has no CornerRadius property.");

        return new Style(x => x.OfType<T>())
        {
            Setters = { new Setter(property, new CornerRadius(6)) }
        };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _provider = BuildServiceProvider();

        var viewModel = _provider.GetRequiredService<MainWindowViewModel>();

        // FR-013: reinstate an interrupted run before the window is shown, so the user
        // sees where they left off rather than a reset clock.
        _provider.GetRequiredService<SessionPersistenceService>()
            .StartTracking(_provider.GetRequiredService<ISettingsService>().Current);

        // Begin logging finished sessions to the local history file.
        _provider.GetRequiredService<SessionHistoryService>().StartTracking();

        // Begin enforcing the blocked-apps list once a session is running. A no-op when
        // unsupported (Windows, or macOS without Accessibility access granted yet).
        _provider.GetRequiredService<AppBlockingService>().StartTracking();

        // Begin auto-pausing on idle once a session is running.
        _provider.GetRequiredService<IdleAutoPauseService>().StartTracking();

        _placement = _provider.GetRequiredService<IWindowPlacementService>();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Closing the window hides to the tray instead of quitting; the app only
            // exits via the tray menu's Quit item (TrayService.Shutdown). The Exit
            // event can't be used for this — it fires during shutdown and its args
            // have no way to cancel.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var window = new MainWindow { DataContext = viewModel };
            _mainWindow = window;

            window.Closing += (sender, e) =>
            {
                e.Cancel = true;
                window.Hide();
            };

            // Fires once the native window actually exists, and only once — the
            // suppressed-animation setting lives on that same native window, which the
            // Hide()/Show() cycle above reuses rather than recreating, so this doesn't
            // need to run again on every later appearance.
            window.Opened += (sender, e) =>
                _provider!.GetRequiredService<IWindowAnimationService>().DisableShowHideAnimation(window);

            // Accessibility permission can be granted in System Settings while the app is
            // running, with no notification back to us — re-check whenever the window
            // regains focus, which covers the "went to grant it, came back" path.
            window.Activated += (sender, e) => viewModel.RefreshAppBlockingSupport();

            // Minimising goes to the tray too, not just closing — the app should never sit
            // in the dock/taskbar as a second copy of the tray icon.
            window.PropertyChanged += (sender, e) =>
            {
                if (e.Property == Window.WindowStateProperty
                    && window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = WindowState.Normal;
                    window.Hide();
                }
            };

            desktop.MainWindow = window;

            // Assigning MainWindow makes the classic desktop lifetime show it automatically
            // once initialization finishes, but with its own default placement — not
            // primary-screen-aware, and not the same spot ShowCentered puts every later
            // appearance of this same window. Showing it here instead means the very first
            // appearance lands consistently with every later one.
            _placement?.ShowCentered(window);

            // A standalone widget rather than part of MainWindow: it has to stay on top
            // and visible even while MainWindow is hidden to the tray, which is the
            // normal state once a session is running.
            _miniTimerWindow = new MiniTimerWindow { DataContext = viewModel };
            WireMiniTimer(viewModel);

            PrewarmSecondaryWindows();

            WireTray(viewModel);
            WireGlobalHotkeys(viewModel);

            viewModel.AlertRequested += (sender, e) => ShowAlert(e.Heading, e.Body);
            viewModel.ShowHistoryRequested += (sender, e) => Dispatcher.UIThread.Post(ShowHistory);
            viewModel.ShowBlockedAppsRequested += (sender, e) => Dispatcher.UIThread.Post(ShowBlockedApps);

            // Storage and permission failures raised from the application layer.
            _provider.GetRequiredService<UserAlerts>().AlertRaised +=
                (sender, e) => ShowAlert(e.Heading, e.Body);

            // Another launch asking us to surface.
            if (InstanceGuard is not null)
            {
                InstanceGuard.ActivationRequested += (sender, e) =>
                    Dispatcher.UIThread.Post(ShowMainWindow);
            }

            // Disposing the container flushes the debounced settings write, writes a
            // final session snapshot and removes the tray icon.
            desktop.Exit += (sender, e) =>
            {
                _provider?.Dispose();
                InstanceGuard?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Ties the widget's visibility to the session rather than to any window state: it
    /// should float above the desktop for the whole run, including while MainWindow sits
    /// hidden in the tray, and disappear the instant the session goes idle.
    /// </summary>
    private void WireMiniTimer(MainWindowViewModel viewModel)
    {
        if (_miniTimerWindow is null)
        {
            return;
        }

        viewModel.PropertyChanged += (sender, e) =>
        {
            if (e.PropertyName != nameof(MainWindowViewModel.IsSessionActive))
            {
                return;
            }

            Dispatcher.UIThread.Post(() => SetMiniTimerVisible(viewModel.IsSessionActive));
        };

        // FR-013 may have already restored an interrupted run by this point, so reflect
        // that immediately rather than waiting for the next tick.
        SetMiniTimerVisible(viewModel.IsSessionActive);
    }

    private void SetMiniTimerVisible(bool visible)
    {
        if (_miniTimerWindow is null)
        {
            return;
        }

        if (visible)
        {
            _miniTimerWindow.Show();
        }
        else
        {
            _miniTimerWindow.Hide();
        }
    }

    private void WireTray(MainWindowViewModel viewModel)
    {
        if (_provider?.GetRequiredService<ITrayService>() is not TrayService tray)
        {
            return;
        }

        // The menu drives the same commands the window does, so the two can't diverge.
        tray.StartBreakRequested += (sender, e) => Run(viewModel.StartBreakCommand);
        tray.PredefinedRequested += (sender, minutes) => Dispatcher.UIThread.Post(() =>
        {
            if (viewModel.StartPredefinedCommand.CanExecute(minutes))
            {
                viewModel.StartPredefinedCommand.Execute(minutes);
            }
        });
        tray.PauseRequested += (sender, e) => Run(viewModel.PauseCommand);
        tray.ResumeRequested += (sender, e) => Run(viewModel.ResumeCommand);
        tray.SkipRequested += (sender, e) => Run(viewModel.SkipCommand);
        tray.ResetRequested += (sender, e) => Run(viewModel.ResetCommand);
        tray.StopRequested += (sender, e) => Run(viewModel.StopCommand);

        tray.OpenRequested += (sender, e) => Dispatcher.UIThread.Post(ShowMainWindow);

        tray.ShowHistoryRequested += (sender, e) => Dispatcher.UIThread.Post(ShowHistory);

        // Goes through BlockedAppsViewModel (not straight to ISettingsService) so that if
        // the Manage Blocked Apps window is open, it reflects the change immediately —
        // same singleton instance either way.
        tray.BlockFrontmostAppRequested += (sender, e) => Dispatcher.UIThread.Post(() =>
        {
            var blocked = _provider!.GetRequiredService<BlockedAppsViewModel>().BlockFrontmostApp();
            var notifications = _provider.GetRequiredService<INotificationService>();

            notifications.ShowNotification(
                blocked is not null ? "App blocked" : "Nothing to block",
                blocked is not null
                    ? $"{blocked.DisplayName} will be hidden during focus sessions."
                    : "Couldn't tell which app was frontmost, or it's already blocked.");
        });
    }

    /// <summary>
    /// Fixed OS-level shortcuts (see README) drive the same commands the tray and window
    /// do, so they can never fall out of sync with what's actually available. Unlike the
    /// tray — which can show separate Pause/Resume items — a hotkey can't display state, so
    /// the start/pause combination is a single toggle: whichever of Start/Pause/Resume is
    /// currently enabled is the one it fires.
    /// </summary>
    private void WireGlobalHotkeys(MainWindowViewModel viewModel)
    {
        if (_provider?.GetRequiredService<IGlobalHotkeys>() is not { } hotkeys)
        {
            return;
        }

        hotkeys.StartPauseRequested += (sender, e) => Dispatcher.UIThread.Post(() =>
        {
            if (viewModel.StartCommand.CanExecute(null))
            {
                viewModel.StartCommand.Execute(null);
            }
            else if (viewModel.PauseCommand.CanExecute(null))
            {
                viewModel.PauseCommand.Execute(null);
            }
            else if (viewModel.ResumeCommand.CanExecute(null))
            {
                viewModel.ResumeCommand.Execute(null);
            }
        });

        hotkeys.StopRequested += (sender, e) => Run(viewModel.StopCommand);
        hotkeys.SkipRequested += (sender, e) => Run(viewModel.SkipCommand);
    }

    /// <summary>
    /// Creates and immediately hides HistoryWindow/BlockedAppsWindow — unlike MiniTimerWindow
    /// (which is genuinely needed from the moment a session starts), neither is needed until
    /// the user asks for it, but each still needs to exist this early: their Opened handler is
    /// what sets NSWindowAnimationBehaviorNone, and setting that during the same Show() call
    /// that's already playing the default animation loses the race — the fade had already
    /// started by the time Opened fired. Doing that here, once, well before either window's
    /// first real appearance, is what makes the very first open animation-free too, not just
    /// every one after it. ShowActivated=false plus an immediate Hide() creates the native
    /// window without stealing focus from MainWindow or (in practice, run back-to-back like
    /// this) surviving to a rendered frame.
    /// </summary>
    private void PrewarmSecondaryWindows()
    {
        _historyWindow = CreateHistoryWindow();
        PrewarmWindow(_historyWindow);

        _blockedAppsWindow = CreateBlockedAppsWindow();
        PrewarmWindow(_blockedAppsWindow);
    }

    private static void PrewarmWindow(Window window)
    {
        window.ShowActivated = false;
        window.Show();
        window.Hide();
        window.ShowActivated = true;
    }

    private HistoryWindow CreateHistoryWindow()
    {
        var window = new HistoryWindow
        {
            DataContext = _provider!.GetRequiredService<HistoryViewModel>()
        };

        window.Closing += (sender, e) =>
        {
            e.Cancel = true;
            window.Hide();
        };

        // Same reasoning as MainWindow's own Opened handler above.
        window.Opened += (sender, e) =>
            _provider!.GetRequiredService<IWindowAnimationService>().DisableShowHideAnimation(window);

        // "‹ Back" or Escape — same as closing the window, but also brings MainWindow
        // forward, so leaving History reads as returning to the app's home screen rather
        // than just dismissing a report.
        window.NavigateBackRequested += (sender, e) =>
        {
            window.Hide();
            ShowMainWindow();
        };

        return window;
    }

    private BlockedAppsWindow CreateBlockedAppsWindow()
    {
        var window = new BlockedAppsWindow
        {
            DataContext = _provider!.GetRequiredService<BlockedAppsViewModel>()
        };

        window.Closing += (sender, e) =>
        {
            e.Cancel = true;
            window.Hide();
        };

        // Same reasoning as MainWindow's own Opened handler above.
        window.Opened += (sender, e) =>
            _provider!.GetRequiredService<IWindowAnimationService>().DisableShowHideAnimation(window);

        // Same reasoning as HistoryWindow.NavigateBackRequested above.
        window.NavigateBackRequested += (sender, e) =>
        {
            window.Hide();
            ShowMainWindow();
        };

        return window;
    }

    /// <summary>
    /// PrewarmSecondaryWindows already created this — reused rather than recreated on every
    /// open so <see cref="HistoryViewModel"/>'s selected range survives between visits in the
    /// same run.
    /// </summary>
    private void ShowHistory()
    {
        if (_historyWindow is null)
        {
            return;
        }

        // Picks up anything logged since the window was last opened.
        if (_historyWindow.DataContext is HistoryViewModel viewModel
            && viewModel.RefreshCommand.CanExecute(null))
        {
            viewModel.RefreshCommand.Execute(null);
        }

        // Same spot MainWindow itself opens in — all three windows sharing one fixed
        // position, on top of already sharing one size, is what actually sells "this feels
        // like one app" rather than three windows that happen to be the same shape.
        _placement?.ShowCentered(_historyWindow);
    }

    /// <summary>
    /// PrewarmSecondaryWindows already created this — reused rather than recreated on every
    /// open so BlockedAppsViewModel's picker state (search text, which checkboxes are ticked)
    /// survives between visits in the same run.
    /// </summary>
    private void ShowBlockedApps()
    {
        if (_blockedAppsWindow is null)
        {
            return;
        }

        // Same reasoning as ShowHistory above.
        _placement?.ShowCentered(_blockedAppsWindow);
    }

    /// <summary>
    /// All three windows (this one, History, BlockedApps — see ShowHistory/ShowBlockedApps
    /// above) open centred on the primary display via the same ShowCentered call, so
    /// switching between them never means the next one landing somewhere different.
    /// </summary>
    private void ShowMainWindow()
    {
        if (_mainWindow is not null)
        {
            _placement?.ShowCentered(_mainWindow);
        }
    }

    /// <summary>
    /// Menu clicks arrive on the platform's own thread; commands touch bound state, so
    /// they have to be executed on the dispatcher.
    /// </summary>
    private static void Run(System.Windows.Input.ICommand command) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (command.CanExecute(null))
            {
                command.Execute(null);
            }
        });

    private static void ShowAlert(string heading, string body) =>
        Dispatcher.UIThread.Post(() => new AlertWindow(heading, body).Show());

    private static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();

        // Injected rather than taken from TimeProvider.System directly so the domain
        // tests can drive the engine with a fake clock.
        services.AddSingleton(TimeProvider.System);

        // Domain
        services.AddSingleton<ITimerEngine, TimerEngine>();

        // Application
        services.AddSingleton<ITimerService, TimerService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<SessionPersistenceService>();
        services.AddSingleton<SessionHistoryService>();

        // Registered twice for the same reason as UserAlerts below: AppBlockingService
        // needs the concrete type to call StartTracking(), the Settings UI depends on
        // the interface.
        services.AddSingleton<AppBlockingService>();
        services.AddSingleton<IAppBlockingService>(sp => sp.GetRequiredService<AppBlockingService>());
        services.AddSingleton<IdleAutoPauseService>();

        // Infrastructure
        services.AddSingleton<IConfigStorage>(_ =>
            new JsonConfigStorage(JsonConfigStorage.DefaultPath()));
        services.AddSingleton<ISessionStateStorage>(_ =>
            new JsonSessionStateStorage(JsonSessionStateStorage.DefaultPath()));
        services.AddSingleton<ISessionHistoryStore>(_ =>
            new JsonLinesSessionHistoryStore(JsonLinesSessionHistoryStore.DefaultPath()));
        services.AddSingleton<IAppLogger>(_ =>
            new FileAppLogger(FileAppLogger.DefaultDirectory()));

        // Presentation. The tray is one Avalonia implementation for every OS; the rest
        // are switched per target framework.
        services.AddSingleton<ITrayService, TrayService>();
        services.AddSingleton<IFilePickerService, FilePickerService>();

        // Registered twice on purpose: services depend on the interface, while App needs
        // the concrete type to subscribe to its event.
        services.AddSingleton<UserAlerts>();
        services.AddSingleton<IUserAlerts>(sp => sp.GetRequiredService<UserAlerts>());
        services.AddSingleton<IWindowPlacementService, WindowPlacementService>();
#if WINDOWS
        services.AddSingleton<INotificationService, WindowsNotificationService>();
        services.AddSingleton<IAudioPlayer, WindowsAudioPlayer>();
        services.AddSingleton<IStartupService, WindowsStartupService>();
        services.AddSingleton<IIdleTimeProvider, WindowsIdleTimeProvider>();
        services.AddSingleton<IMenuBarCountdown, NoopMenuBarCountdown>();
        services.AddSingleton<IAppBlockingMonitor, NoopAppBlockingMonitor>();
        services.AddSingleton<IGlobalHotkeys, WindowsGlobalHotkeys>();
        services.AddSingleton<IWindowAnimationService, NoopWindowAnimationService>();
#else
        services.AddSingleton<INotificationService, MacNotificationService>();
        services.AddSingleton<IAudioPlayer, MacAudioPlayer>();
        services.AddSingleton<IStartupService, MacStartupService>();
        services.AddSingleton<IIdleTimeProvider, MacIdleTimeProvider>();
        services.AddSingleton<IMenuBarCountdown, NativeMenuBarCountdown>();
        services.AddSingleton<IAppBlockingMonitor, MacAppBlockingMonitor>();
        services.AddSingleton<IGlobalHotkeys, MacGlobalHotkeys>();
        services.AddSingleton<IWindowAnimationService, MacWindowAnimationService>();
#endif

        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<BlockedAppsViewModel>();

        return services.BuildServiceProvider();
    }
}
