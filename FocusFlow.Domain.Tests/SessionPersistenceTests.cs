using FocusFlow.Application.Services;
using FocusFlow.Domain.Engines;
using FocusFlow.Domain.Models;
using FocusFlow.Infrastructure.Storage;
using Microsoft.Extensions.Time.Testing;

namespace FocusFlow.Domain.Tests;

/// <summary>
/// FR-013 end to end: engine -> service -> disk -> new engine, on a fake clock.
/// </summary>
public class SessionPersistenceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "focusflow-session-tests", Guid.NewGuid().ToString("N"));

    private string StatePath => Path.Combine(_directory, "session.json");

    private static TimerConfig Config() => new()
    {
        StudyDuration = TimeSpan.FromMinutes(25),
        BreakDuration = TimeSpan.FromMinutes(5)
    };

    [Fact]
    public void StateRoundTripsThroughDisk()
    {
        var storage = new JsonSessionStateStorage(StatePath);
        var state = new SessionState
        {
            Mode = TimerMode.Study,
            RemainingTime = TimeSpan.FromMinutes(13),
            CurrentSession = 2,
            IsPaused = false
        };

        storage.Save(state);
        var loaded = storage.Load();

        Assert.NotNull(loaded);
        Assert.Equal(TimerMode.Study, loaded!.Mode);
        Assert.Equal(TimeSpan.FromMinutes(13), loaded.RemainingTime);
        Assert.Equal(2, loaded.CurrentSession);
    }

    [Fact]
    public void LoadReturnsNullWhenNothingWasSaved()
    {
        Assert.Null(new JsonSessionStateStorage(StatePath).Load());
    }

    [Fact]
    public void ClearRemovesTheSavedSession()
    {
        var storage = new JsonSessionStateStorage(StatePath);
        storage.Save(new SessionState { Mode = TimerMode.Study, RemainingTime = TimeSpan.FromMinutes(1) });

        storage.Clear();

        Assert.Null(storage.Load());
    }

    [Fact]
    public async Task ARunningSessionIsMirroredToDiskAndResumesOnTheNextLaunch()
    {
        var storage = new JsonSessionStateStorage(StatePath);
        var clock = new FakeTimeProvider();

        // --- "first launch": run for a while, then die without a clean shutdown.
        var engine = new TimerEngine(clock);
        var service = new TimerService(engine);
        var persistence = new SessionPersistenceService(service, storage, clock);
        persistence.StartTracking(Config());

        await service.StartAsync(Config());
        for (var i = 0; i < 90; i++)
        {
            clock.Run(TimeSpan.FromSeconds(1));
        }

        var beforeCrash = service.CurrentState.RemainingTime;
        Assert.Equal(TimeSpan.FromMinutes(25) - TimeSpan.FromSeconds(90), beforeCrash);

        // No Dispose/Flush — this is the crash case, so only the throttled writes landed.
        Assert.NotNull(storage.Load());

        // --- "second launch": a fresh engine picks the session back up.
        var engine2 = new TimerEngine(clock);
        var service2 = new TimerService(engine2);
        var persistence2 = new SessionPersistenceService(service2, storage, clock);

        var restored = persistence2.StartTracking(Config());

        Assert.True(restored);
        Assert.Equal(TimerMode.Study, service2.CurrentState.Mode);
        Assert.True(service2.CurrentState.IsPaused);

        // Within one write interval of where it stopped.
        var drift = (beforeCrash - service2.CurrentState.RemainingTime).Duration();
        Assert.True(drift <= TimeSpan.FromSeconds(5), $"drift was {drift}");

        persistence.Dispose();
        persistence2.Dispose();
    }

    [Fact]
    public async Task StoppingTheTimerClearsTheSavedSession()
    {
        var storage = new JsonSessionStateStorage(StatePath);
        var clock = new FakeTimeProvider();

        var service = new TimerService(new TimerEngine(clock));
        using var persistence = new SessionPersistenceService(service, storage, clock);
        persistence.StartTracking(Config());

        await service.StartAsync(Config());
        clock.Run(TimeSpan.FromSeconds(10));
        Assert.NotNull(storage.Load());

        service.Stop();

        // A finished run must not be offered for resume on the next launch.
        Assert.Null(storage.Load());
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
