using FocusFlow.Application.Interfaces;

namespace FocusFlow.Infrastructure.Logging;

/// <summary>
/// Appends timestamped lines to a daily log file under the app's own data folder.
/// </summary>
/// <remarks>
/// Built to survive being the thing that reports its own failure: every public method
/// swallows its own I/O errors rather than throwing, because a logger that can crash the
/// app it's trying to diagnose has made things worse, not better.
/// </remarks>
public sealed class FileAppLogger : IAppLogger
{
    /// <summary>Old files are pruned on startup rather than kept forever.</summary>
    private const int RetainDays = 14;

    private readonly string _directory;
    private readonly Lock _gate = new();

    public FileAppLogger(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;

        try
        {
            Directory.CreateDirectory(_directory);
            PruneOldLogs();
        }
        catch
        {
            // A logger that can't prepare its own folder still shouldn't stop the app
            // from starting — every Write below is equally forgiving.
        }
    }

    /// <summary>
    /// %APPDATA%\FocusFlow\logs on Windows, ~/Library/Application Support/FocusFlow/logs
    /// on macOS — alongside config.json and history.jsonl, not somewhere new.
    /// </summary>
    public static string DefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolderOption.Create),
        "FocusFlow",
        "logs");

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message) => Write("WARN", message, null);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}"
            + (exception is null ? string.Empty : Environment.NewLine + exception);

        try
        {
            lock (_gate)
            {
                // Resolved per write, not cached: FocusFlow is designed to keep running
                // for days across sleep/wake, and a process left open across midnight
                // should still roll onto the next day's file instead of one file growing
                // for as long as the app happens to stay open.
                var path = Path.Combine(_directory, $"focusflow-{DateTime.Now:yyyy-MM-dd}.log");
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch
        {
            // A logger must never be the reason the app fails.
        }
    }

    private void PruneOldLogs()
    {
        var cutoff = DateTime.Now.AddDays(-RetainDays);
        foreach (var file in Directory.EnumerateFiles(_directory, "focusflow-*.log"))
        {
            if (File.GetLastWriteTime(file) < cutoff)
            {
                File.Delete(file);
            }
        }
    }
}
