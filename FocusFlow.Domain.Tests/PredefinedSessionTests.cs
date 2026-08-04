using FocusFlow.Domain.Engines;
using FocusFlow.Domain.Interfaces;
using FocusFlow.Domain.Models;
using Microsoft.Extensions.Time.Testing;

namespace FocusFlow.Domain.Tests;

/// <summary>
/// A predefined session is one fixed-length focus block that stops when it's done —
/// no break, no next session, nothing auto-started.
/// </summary>
public class PredefinedSessionTests
{
    private static (TimerEngine Engine, FakeTimeProvider Clock) Build()
    {
        var clock = new FakeTimeProvider();
        return (new TimerEngine(clock), clock);
    }

    /// <summary>Everything switched on, so the test proves the one-shot rule overrides it.</summary>
    private static TimerConfig CyclingConfig() => new()
    {
        StudyDuration = TimeSpan.FromMinutes(25),
        BreakDuration = TimeSpan.FromMinutes(5),
        AutoStartBreak = true,
        AutoStartStudy = true,
        InfiniteMode = true
    };

    [Theory]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(60)]
    public void StartsAtTheRequestedLengthRatherThanTheConfiguredOne(int minutes)
    {
        var (engine, _) = Build();

        engine.StartPredefined(CyclingConfig(), TimeSpan.FromMinutes(minutes));

        var state = engine.CurrentState;
        Assert.Equal(TimerMode.Study, state.Mode);
        Assert.Equal(TimeSpan.FromMinutes(minutes), state.RemainingTime);
        Assert.False(state.IsPaused);
    }

    [Fact]
    public void AShortSessionIsNotClampedByTheConfiguredMinimum()
    {
        // 15 sits well inside the 1-120 range, but this pins the shortest offered option
        // against the normalisation that runs over saved settings.
        var (engine, clock) = Build();

        engine.StartPredefined(CyclingConfig(), TimeSpan.FromMinutes(15));
        clock.Run(TimeSpan.FromMinutes(14));

        Assert.Equal(TimeSpan.FromMinutes(1), engine.CurrentState.RemainingTime);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(60)]
    public void EndsAndStopsInsteadOfStartingABreak(int minutes)
    {
        var (engine, clock) = Build();
        SessionEndedEventArgs? ended = null;
        engine.SessionEnded += (_, e) => ended = e;

        engine.StartPredefined(CyclingConfig(), TimeSpan.FromMinutes(minutes));
        clock.Run(TimeSpan.FromMinutes(minutes));

        Assert.NotNull(ended);
        Assert.Equal(TimerMode.Study, ended!.CompletedMode);
        Assert.Equal(TimerMode.Idle, ended.NextMode);
        Assert.True(ended.RunCompleted);
        Assert.Equal(TimerMode.Idle, engine.CurrentState.Mode);
    }

    [Fact]
    public void AutoStartIsIgnoredSoNothingFollowsIt()
    {
        // The config has both auto-start flags on; a predefined run must still stop dead.
        var (engine, clock) = Build();

        engine.StartPredefined(CyclingConfig(), TimeSpan.FromMinutes(45));
        clock.Run(TimeSpan.FromMinutes(45));
        clock.Run(TimeSpan.FromMinutes(10));

        Assert.Equal(TimerMode.Idle, engine.CurrentState.Mode);
    }

    [Fact]
    public void InfiniteModeDoesNotMakeItRepeat()
    {
        var (engine, clock) = Build();
        var ends = 0;
        engine.SessionEnded += (_, _) => ends++;

        engine.StartPredefined(CyclingConfig(), TimeSpan.FromMinutes(45));
        clock.Run(TimeSpan.FromMinutes(45));
        clock.Run(TimeSpan.FromMinutes(45));

        Assert.Equal(1, ends);
    }

    [Fact]
    public void ItIsRecordedInHistoryAsACompletedFocusSession()
    {
        var (engine, clock) = Build();
        SessionEndedEventArgs? ended = null;
        engine.SessionEnded += (_, e) => ended = e;

        engine.StartPredefined(CyclingConfig(), TimeSpan.FromMinutes(60));
        clock.Run(TimeSpan.FromMinutes(60));

        var record = ended!.ToRecord(DateTimeOffset.UtcNow);
        Assert.Equal(TimerMode.Study, record.Mode);
        Assert.Equal(SessionOutcome.Completed, record.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(60), record.PlannedDuration);
        Assert.Equal(TimeSpan.FromMinutes(60), record.ActualDuration);
    }

    [Fact]
    public void StoppingEarlyRecordsTheTimeActuallySpent()
    {
        var (engine, clock) = Build();
        SessionEndedEventArgs? ended = null;
        engine.SessionEnded += (_, e) => ended = e;

        engine.StartPredefined(CyclingConfig(), TimeSpan.FromMinutes(60));
        clock.Run(TimeSpan.FromMinutes(12));
        engine.Stop();

        Assert.Equal(SessionOutcome.Stopped, ended!.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(12), ended.ActualDuration);
        Assert.Equal(TimeSpan.FromMinutes(60), ended.PlannedDuration);
    }

    [Fact]
    public void TheStoredConfigurationIsNotChangedByRunningOne()
    {
        // The 60 belongs to this run, not to the user's saved preferences.
        var (engine, clock) = Build();
        var config = CyclingConfig();

        engine.StartPredefined(config, TimeSpan.FromMinutes(60));
        clock.Run(TimeSpan.FromMinutes(60));

        Assert.Equal(TimeSpan.FromMinutes(25), config.StudyDuration);
    }

    [Fact]
    public void AStandaloneBreakStillEndsTheRunToo()
    {
        // Same one-shot rule, exercised through the older entry point.
        var (engine, clock) = Build();

        engine.StartBreak(CyclingConfig());
        clock.Run(TimeSpan.FromMinutes(5));

        Assert.Equal(TimerMode.Idle, engine.CurrentState.Mode);
    }
}
