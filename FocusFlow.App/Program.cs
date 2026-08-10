using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using FocusFlow.App.Services;
using FocusFlow.Infrastructure.Logging;

namespace FocusFlow.App;

internal class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Built directly rather than resolved from the DI container: the container can
        // crash before it exists, gets disposed before the process actually exits, and a
        // fatal error on a background thread has no guarantee it is even still alive when
        // it fires. A crash logger that depends on any of that isn't one to depend on.
        var crashLogger = new FileAppLogger(FileAppLogger.DefaultDirectory());

        // The last line of defence: without this, an exception on any thread other than
        // the UI thread's own event handlers kills the process with nothing left behind
        // to say why. This can't stop the crash — by the time it fires the runtime has
        // already decided to terminate — but it can make sure the crash isn't silent.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            crashLogger.Error("Unhandled exception", e.ExceptionObject as Exception);

        // A faulted Task whose exception nobody awaited or observed. .NET no longer
        // crashes the process for these, which is exactly why they need logging — they
        // would otherwise fail completely silently instead of just quietly.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            crashLogger.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        crashLogger.Info("FocusFlow starting");

        // Claim the single-instance slot before Avalonia starts. Two FocusFlows would each
        // own a tray icon and each write the session and history files, corrupting both.
        // Deliberately before AppBuilder: there is no point spinning up a UI we are about
        // to tear down.
        var guard = new SingleInstanceGuard(DataDirectory(), crashLogger);

        if (!guard.TryAcquire())
        {
            // The running instance has been signalled to surface; nothing left to do here.
            crashLogger.Info("FocusFlow already running — surfacing the existing instance");
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
            crashLogger.Info("FocusFlow exiting");
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
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // LSUIElement in Info.plist is not enough on its own: Avalonia sets the macOS
            // activation policy itself during startup and puts the app back in the Dock.
            // FocusFlow lives in the menu bar, so it should not also own a Dock tile.
            .With(new MacOSPlatformOptions { ShowInDock = false })
#if DEBUG
            //.WithDeveloperTools()
#endif
            .LogToTrace();

        // Bundled only where it's needed: macOS already renders San Francisco as the
        // platform default, and bundling Inter over it is what made the app look like a
        // Windows port. Windows has no comparable system default worth trusting, so it
        // keeps Inter.
        return OperatingSystem.IsMacOS() ? builder : builder.WithInterFont();
    }
}
