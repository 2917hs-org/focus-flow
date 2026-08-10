using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FocusFlow.Application.Interfaces;

namespace FocusFlow.App.Services;

/// <summary>
/// Ensures only one FocusFlow runs, and hands activation to the instance already running.
/// </summary>
/// <remarks>
/// <para>
/// A second launch of a timer app is never what the user wants — two instances would each
/// own a tray icon, each write the session file, and quietly corrupt each other's history.
/// </para>
/// <para>
/// Uses an exclusive lock on a file rather than a named mutex, because a mutex is a
/// Windows-only concept in practice: .NET maps named mutexes onto files on Unix with
/// awkward lifetime semantics. An exclusively-opened file works identically on both, and
/// the OS releases the handle if the process dies, so a crash can't lock the user out.
/// </para>
/// </remarks>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly string _lockPath;
    private readonly string _signalPath;
    private readonly IAppLogger? _logger;

    private FileStream? _lock;
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    public SingleInstanceGuard(string directory, IAppLogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);

        _lockPath = Path.Combine(directory, "instance.lock");
        _signalPath = Path.Combine(directory, "activate.signal");
        _logger = logger;
    }

    /// <summary>Raised when another launch asked this instance to show itself.</summary>
    public event EventHandler? ActivationRequested;

    /// <summary>
    /// Claims the single-instance slot. Returns false when another instance already holds
    /// it — in which case that instance has been asked to surface and this one should exit.
    /// </summary>
    public bool TryAcquire()
    {
        // Retry briefly before concluding another instance is running. Quitting and
        // relaunching immediately would otherwise fail silently: the outgoing process can
        // still hold the lock for a moment after its window has gone.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0)
            {
                Thread.Sleep(250);
            }

            if (TryOpenLock())
            {
                StartWatchingForSignals();
                return true;
            }
        }

        // Genuinely already running: poke it, then let the caller bow out.
        SignalRunningInstance();
        return false;
    }

    private bool TryOpenLock()
    {
        try
        {
            _lock = new FileStream(
                _lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            // The expected path when another instance is already running — not a
            // failure, so not logged here. Program logs the outcome of the retry loop
            // this feeds into instead.
            return false;
        }
        catch (UnauthorizedAccessException e)
        {
            // Can't tell either way; better to run than to refuse to start.
            _logger?.Warn($"Could not tell whether another instance holds the lock: {e.Message}");
            return true;
        }

        return true;
    }

    private void SignalRunningInstance()
    {
        try
        {
            File.WriteAllText(_signalPath, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (Exception e)
        {
            // Worst case the first instance stays hidden; the user clicks the tray icon.
            _logger?.Warn($"Signalling the running instance failed: {e.Message}");
        }
    }

    private void StartWatchingForSignals()
    {
        try
        {
            var directory = Path.GetDirectoryName(_signalPath)!;

            _watcher = new FileSystemWatcher(directory, Path.GetFileName(_signalPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };

            _watcher.Created += OnSignal;
            _watcher.Changed += OnSignal;
        }
        catch (Exception e)
        {
            // Single-instance still holds; only the "focus the first window" nicety is lost.
            _logger?.Warn($"Watching for activation signals failed: {e.Message}");
        }
    }

    private void OnSignal(object? sender, FileSystemEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        ActivationRequested?.Invoke(this, EventArgs.Empty);

        // Clear it so the next launch produces a fresh event rather than being swallowed
        // as a duplicate.
        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            try
            {
                File.Delete(_signalPath);
            }
            catch (Exception e)
            {
                // A leftover signal file at worst causes one redundant activation later.
                _logger?.Warn($"Deleting the activation signal failed: {e.Message}");
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_watcher is not null)
        {
            _watcher.Created -= OnSignal;
            _watcher.Changed -= OnSignal;
            _watcher.Dispose();
        }

        // FileOptions.DeleteOnClose removes the lock file as this handle closes.
        _lock?.Dispose();
    }
}
