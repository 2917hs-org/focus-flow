using FocusFlow.Infrastructure.Logging;

namespace FocusFlow.Domain.Tests;

/// <summary>
/// <see cref="FileAppLogger"/> is the only record of what the app did on a machine nobody
/// can attach a debugger to, so it has to actually land on disk — and it has to survive a
/// disk that won't cooperate, since the last thing a crash needs is a second one.
/// </summary>
public class FileAppLoggerTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "focusflow-tests", Guid.NewGuid().ToString("N"));

    private string TodaysLogFile => Path.Combine(_directory, $"focusflow-{DateTime.Now:yyyy-MM-dd}.log");

    [Fact]
    public void Info_CreatesTheDirectoryAndWritesTheMessage()
    {
        var logger = new FileAppLogger(_directory);

        logger.Info("FocusFlow starting");

        Assert.True(File.Exists(TodaysLogFile));
        Assert.Contains("FocusFlow starting", File.ReadAllText(TodaysLogFile));
    }

    [Fact]
    public void Error_IncludesTheLevelAndTheExceptionDetail()
    {
        var logger = new FileAppLogger(_directory);

        logger.Error("Settings save failed", new InvalidOperationException("disk is read-only"));

        var contents = File.ReadAllText(TodaysLogFile);
        Assert.Contains("[ERROR]", contents);
        Assert.Contains("Settings save failed", contents);
        Assert.Contains("disk is read-only", contents);
    }

    [Fact]
    public void MultipleWrites_AllLandInTheSameFile()
    {
        var logger = new FileAppLogger(_directory);

        logger.Info("first");
        logger.Warn("second");
        logger.Error("third");

        var lines = File.ReadAllLines(TodaysLogFile);
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public void OldLogFiles_ArePrunedOnConstruction()
    {
        Directory.CreateDirectory(_directory);
        var stale = Path.Combine(_directory, "focusflow-2000-01-01.log");
        File.WriteAllText(stale, "ancient");
        File.SetLastWriteTime(stale, DateTime.Now.AddDays(-30));

        _ = new FileAppLogger(_directory);

        Assert.False(File.Exists(stale));
    }

    [Fact]
    public void RecentLogFiles_SurviveConstruction()
    {
        Directory.CreateDirectory(_directory);
        var recent = Path.Combine(_directory, "focusflow-2099-01-01.log");
        File.WriteAllText(recent, "not stale yet");

        _ = new FileAppLogger(_directory);

        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void Error_OnAnUnwritableDirectory_DoesNotThrow()
    {
        // The directory is a file, not a folder, so every write below fails — the point
        // is that callers never see that failure.
        var blocked = Path.Combine(_directory, "blocked");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(blocked, "not a directory");
        var logger = new FileAppLogger(blocked);

        var exception = Record.Exception(() => logger.Error("this must not throw"));

        Assert.Null(exception);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
