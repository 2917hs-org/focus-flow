using FocusFlow.Domain.Engines;
using FocusFlow.Domain.Interfaces;
using FocusFlow.Domain.Models;
using Microsoft.Extensions.Time.Testing;

namespace FocusFlow.Domain.Tests;

/// <summary>
/// FR-101 / FR-104: surviving suspend-resume and system clock changes.
/// </summary>
public class ResilienceTests
{
    private static (TimerEngine Engine, FakeTimeProvider Clock) Build()
    {
        var clock = new FakeTimeProvider();
        return (new TimerEngine(clock), clock);
    }

    private static TimerConfig Config(int study = 25, int @break = 5) => new()
    {
        StudyDuration = TimeSpan.FromMinutes(study),
        BreakDuration = TimeSpan.FromMinutes(@break)
    };

    // ---- FR-101: sleep / wake --------------------------------------------------------

    [Fact]
    public void FR101_TimeSpentAsleepIsNotChargedToTheSession()
    {
        var (engine, clock) = Build();
        engine.Start(Config(study: 25));

        clock.Run(TimeSpan.FromMinutes(5));
        var beforeSleep = engine.CurrentState.RemainingTime;

        // One large jump with no intervening polls is what a suspend looks like to the
        // engine: the callback simply does not run for the duration.
        clock.Advance(TimeSpan.FromHours(3));

        Assert.Equal(beforeSleep, engine.CurrentState.RemainingTime);
    }

    [Fact]
    public void FR101_WakingRaisesSystemResumedWithRoughlyHowLongItSlept()
    {
        var (engine, clock) = Build();
        SystemResumedEventArgs? resumed = null;
        engine.SystemResumed += (_, e) => resumed = e;

        engine.Start(Config());
        clock.Advance(TimeSpan.FromMinutes(30));

        Assert.NotNull(resumed);
        Assert.True(resumed!.SuspendedFor >= TimeSpan.FromMinutes(29), $"was {resumed.SuspendedFor}");
    }

    [Fact]
    public void FR101_TheSessionKeepsRunningAfterWaking()
    {
        var (engine, clock) = Build();
        engine.Start(Config(study: 25));
        clock.Run(TimeSpan.FromMinutes(5));

        clock.Advance(TimeSpan.FromHours(2)); // asleep

        // Counting continues from where it left off, not from a reset.
        clock.Run(TimeSpan.FromMinutes(1));

        Assert.Equal(TimeSpan.FromMinutes(19), engine.CurrentState.RemainingTime);
        Assert.False(engine.CurrentState.IsPaused);
    }

    [Fact]
    public void FR101_ALongSleepDoesNotBlowThroughEverySession()
    {
        // Without suspend handling an 8-hour sleep would fire session-end repeatedly and
        // leave the session counter somewhere meaningless.
        var (engine, clock) = Build();
        var ends = 0;
        engine.SessionEnded += (_, _) => ends++;

        engine.Start(Config(study: 25, @break: 5));
        clock.Advance(TimeSpan.FromHours(8));

        Assert.Equal(0, ends);
        Assert.Equal(1, engine.CurrentState.CurrentSession);
        Assert.Equal(TimerMode.Study, engine.CurrentState.Mode);
    }

    [Fact]
    public void FR101_OrdinaryTicksAreNotMistakenForASuspend()
    {
        var (engine, clock) = Build();
        var resumes = 0;
        engine.SystemResumed += (_, _) => resumes++;

        engine.Start(Config());
        clock.Run(TimeSpan.FromSeconds(30));

        Assert.Equal(0, resumes);
        Assert.Equal(TimeSpan.FromMinutes(25) - TimeSpan.FromSeconds(30), engine.CurrentState.RemainingTime);
    }

    [Fact]
    public void FR101_SleepingWhilePausedChangesNothing()
    {
        var (engine, clock) = Build();
        engine.Start(Config());
        clock.Run(TimeSpan.FromMinutes(2));
        engine.Pause();
        var atPause = engine.CurrentState.RemainingTime;

        clock.Advance(TimeSpan.FromHours(5));

        Assert.Equal(atPause, engine.CurrentState.RemainingTime);
        Assert.True(engine.CurrentState.IsPaused);
    }

    // ---- FR-104: system clock changes ------------------------------------------------

    [Fact]
    public void FR104_MovingTheWallClockForwardDoesNotSkipTheSession()
    {
        var (engine, clock) = Build();
        engine.Start(Config(study: 25));
        clock.Run(TimeSpan.FromMinutes(1));

        var before = engine.CurrentState.RemainingTime;

        // A timezone change, DST jump or NTP correction. Note that FakeTimeProvider moves
        // its wall clock and its timestamp together, so under test this is caught by the
        // FR-101 suspend guard rather than by monotonic immunity; on a real system the
        // monotonic timestamp simply does not move. Either way the session is not skipped,
        // which is the behaviour FR-104 asks for.
        clock.SetUtcNow(clock.GetUtcNow().AddHours(6));

        Assert.Equal(before, engine.CurrentState.RemainingTime);
    }

    // A backward clock change cannot be covered here: FakeTimeProvider.SetUtcNow throws
    // "Cannot go back in time", so the scenario is unreachable from a test. The real
    // guarantee is structural — the engine derives remaining time from GetTimestamp /
    // GetElapsedTime and never reads GetUtcNow for timing, so a wall-clock move in either
    // direction cannot reach the countdown. ComputeRemaining additionally floors elapsed
    // at zero in case a platform's monotonic source ever does step backwards.

    [Fact]
    public void FR104_AClockChangeDoesNotEndTheSession()
    {
        var (engine, clock) = Build();
        var ends = 0;
        engine.SessionEnded += (_, _) => ends++;

        engine.Start(Config(study: 25));
        clock.SetUtcNow(clock.GetUtcNow().AddDays(1));

        Assert.Equal(0, ends);
        Assert.Equal(TimerMode.Study, engine.CurrentState.Mode);
    }
}
