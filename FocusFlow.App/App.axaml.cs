using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FocusFlow.App.Services;
using FocusFlow.App.ViewModels;
using FocusFlow.App.Views;
using FocusFlow.Application.Interfaces;
using FocusFlow.Application.Services;
using FocusFlow.Domain.Engines;
using FocusFlow.Domain.Interfaces;
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

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Closing the window hides to the tray instead of quitting; the app only
            // exits via the tray menu's Exit item (TrayService.Shutdown). The Exit
            // event can't be used for this — it fires during shutdown and its args
            // have no way to cancel.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var window = new MainWindow { DataContext = viewModel };
            window.Closing += (sender, e) =>
            {
                e.Cancel = true;
                window.Hide();
            };
            desktop.MainWindow = window;

            // Disposing the container flushes the debounced settings write, writes a
            // final session snapshot and removes the tray icon.
            desktop.Exit += (sender, e) => _provider?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

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

        // Presentation. The tray is one Avalonia implementation for every OS; the rest
        // are switched per target framework.
        services.AddSingleton<ITrayService, TrayService>();
        services.AddSingleton<IFilePickerService, FilePickerService>();
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
