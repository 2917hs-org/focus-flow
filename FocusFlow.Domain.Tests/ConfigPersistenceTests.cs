using FocusFlow.Application.Services;
using FocusFlow.Domain.Models;
using FocusFlow.Infrastructure.Storage;
using Microsoft.Extensions.Time.Testing;

namespace FocusFlow.Domain.Tests;

/// <summary>
/// Covers the settings round-trip: <see cref="SettingsService"/> over
/// <see cref="JsonConfigStorage"/>.
/// </summary>
/// <remarks>
/// These reach past the Domain into Application/Infrastructure. They live here to avoid a
/// third test project; split them out if that layering starts to matter.
/// </remarks>
public class ConfigPersistenceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "focusflow-tests", Guid.NewGuid().ToString("N"));

    private string ConfigPath => Path.Combine(_directory, "nested", "config.json");

    [Fact]
    public void Save_CreatesTheDirectoryOnFirstRun()
    {
        // Regression: Save used to call File.WriteAllText directly, which throws
        // DirectoryNotFoundException when %APPDATA%\FocusFlow does not exist yet — i.e.
        // for every brand new install.
        var storage = new JsonConfigStorage(ConfigPath);

        storage.Save(new TimerConfig { StudyDuration = TimeSpan.FromMinutes(40) });

        Assert.True(File.Exists(ConfigPath));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEveryField()
    {
        var storage = new JsonConfigStorage(ConfigPath);
        var saved = new TimerConfig
        {
            StudyDuration = TimeSpan.FromMinutes(40),
            BreakDuration = TimeSpan.FromMinutes(12),
            AutoStartBreak = false,
            AutoStartStudy = false,
            AlarmSoundPath = "/System/Library/Sounds/Tink.aiff"
        };

        storage.Save(saved);
        var loaded = storage.Load();

        Assert.Equal(saved.StudyDuration, loaded.StudyDuration);
        Assert.Equal(saved.BreakDuration, loaded.BreakDuration);
        Assert.False(loaded.AutoStartBreak);
        Assert.False(loaded.AutoStartStudy);
        Assert.Equal(saved.AlarmSoundPath, loaded.AlarmSoundPath);
    }

    [Fact]
    public void Load_OnAMissingFile_ReturnsDefaults()
    {
        var loaded = new JsonConfigStorage(ConfigPath).Load();

        Assert.Equal(TimeSpan.FromMinutes(25), loaded.StudyDuration);
        Assert.Equal(TimeSpan.FromMinutes(5), loaded.BreakDuration);
    }

    [Fact]
    public void Load_OnACorruptFile_FallsBackToDefaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, "{ this is not json");

        var loaded = new JsonConfigStorage(ConfigPath).Load();

        Assert.Equal(TimeSpan.FromMinutes(25), loaded.StudyDuration);
    }

    [Fact]
    public void SettingsService_DebouncesThenPersists()
    {
        var clock = new FakeTimeProvider();
        var storage = new JsonConfigStorage(ConfigPath);
        using var settings = new SettingsService(storage, clock);

        settings.Update(c => c.StudyDuration = TimeSpan.FromMinutes(30));
        settings.Update(c => c.StudyDuration = TimeSpan.FromMinutes(45));

        // Still inside the quiet period — nothing written yet.
        Assert.False(File.Exists(ConfigPath));

        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.True(File.Exists(ConfigPath));
        Assert.Equal(TimeSpan.FromMinutes(45), storage.Load().StudyDuration);
    }

    [Fact]
    public void SettingsService_NormalizesOutOfRangeDurations()
    {
        var clock = new FakeTimeProvider();
        using var settings = new SettingsService(new JsonConfigStorage(ConfigPath), clock);

        settings.Update(c => c.StudyDuration = TimeSpan.Zero);

        // A zero duration would otherwise end sessions instantly, in a loop.
        Assert.True(settings.Current.StudyDuration >= TimerConfig.MinDuration);
    }

    [Fact]
    public void SettingsService_DisposeFlushesAPendingWrite()
    {
        var clock = new FakeTimeProvider();
        var storage = new JsonConfigStorage(ConfigPath);

        using (var settings = new SettingsService(storage, clock))
        {
            settings.Update(c => c.BreakDuration = TimeSpan.FromMinutes(9));
        }

        Assert.Equal(TimeSpan.FromMinutes(9), storage.Load().BreakDuration);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
