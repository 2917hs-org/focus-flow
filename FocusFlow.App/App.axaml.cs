using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
    private IWindowPlacementService? _placement;

    /// <summary>Set by Program when another launch was detected and refused.</summary>
    public static SingleInstanceGuard? InstanceGuard { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
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

            // A standalone widget rather than part of MainWindow: it has to stay on top
            // and visible even while MainWindow is hidden to the tray, which is the
            // normal state once a session is running.
            _miniTimerWindow = new MiniTimerWindow { DataContext = viewModel };
            WireMiniTimer(viewModel);

            WireTray(viewModel);

            viewModel.AlertRequested += (sender, e) => ShowAlert(e.Heading, e.Body);

            // Storage and permission failures raised from the application layer.
            _provider.GetRequiredService<UserAlerts>().AlertRaised +=
                (sender, e) => ShowAlert(e.Heading, e.Body);

            // Another launch asking us to surface.
            if (InstanceGuard is not null)
            {
                InstanceGuard.ActivationRequested += (sender, e) =>
                    Dispatcher.UIThread.Post(() => _placement?.ShowOnActiveScreen(window));
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
        tray.StartRequested += (sender, e) => Run(viewModel.StartCommand);
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

        tray.OpenRequested += (sender, e) => Dispatcher.UIThread.Post(() =>
        {
            if (_mainWindow is not null)
            {
                _placement?.ShowOnActiveScreen(_mainWindow);
            }
        });
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

    private void ShowAlert(string heading, string body) =>
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
        services.AddSingleton<IPointerLocator, WindowsPointerLocator>();
#else
        services.AddSingleton<INotificationService, MacNotificationService>();
        services.AddSingleton<IAudioPlayer, MacAudioPlayer>();
        services.AddSingleton<IStartupService, MacStartupService>();
        services.AddSingleton<IPointerLocator, MacPointerLocator>();
#endif

        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }
}
