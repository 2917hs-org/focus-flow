using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using FocusFlow.App.Services;

namespace FocusFlow.App;

internal class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Claim the single-instance slot before Avalonia starts. Two FocusFlows would each
        // own a tray icon and each write the session and history files, corrupting both.
        // Deliberately before AppBuilder: there is no point spinning up a UI we are about
        // to tear down.
        var guard = new SingleInstanceGuard(DataDirectory());

        if (!guard.TryAcquire())
        {
            // The running instance has been signalled to surface; nothing left to do here.
            guard.Dispose();
            return;
        }

        App.InstanceGuard = guard;

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            guard.Dispose();
        }
    }

    private static string DataDirectory() => Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolderOption.Create),
        "FocusFlow");

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // LSUIElement in Info.plist is not enough on its own: Avalonia sets the macOS
            // activation policy itself during startup and puts the app back in the Dock.
            // FocusFlow lives in the menu bar, so it should not also own a Dock tile.
            .With(new MacOSPlatformOptions { ShowInDock = false })
#if DEBUG
            //.WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
