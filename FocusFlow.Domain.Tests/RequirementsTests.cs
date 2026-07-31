using FocusFlow.Domain.Engines;
using FocusFlow.Domain.Interfaces;
using FocusFlow.Domain.Models;
using Microsoft.Extensions.Time.Testing;

namespace FocusFlow.Domain.Tests;

/// <summary>
/// Covers the behaviour added for the FR-xxx requirements table.
/// </summary>
public class RequirementsTests
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

    // ---- FR-001 / FR-002: duration bounds -------------------------------------------

    [Fact]
    public void FR001_StudyDurationIsCappedAt120Minutes()
    {
        var config = new TimerConfig { StudyDuration = TimeSpan.FromMinutes(999) }.Normalized();

        Assert.Equal(TimeSpan.FromMinutes(120), config.StudyDuration);
    }

    [Fact]
    public void FR002_BreakDurationIsCappedAt60Minutes()
    {
        var config = new TimerConfig { BreakDuration = TimeSpan.FromMinutes(999) }.Normalized();

        Assert.Equal(TimeSpan.FromMinutes(60), config.BreakDuration);
    }

    [Fact]
    public void FR001_DurationsBelowOneMinuteAreRaisedToTheMinimum()
    {
        var config = new TimerConfig
        {
            StudyDuration = TimeSpan.FromSeconds(5),
            BreakDuration = TimeSpan.FromSeconds(5)
        }.Normalized();

        Assert.Equal(TimerConfig.MinDuration, config.StudyDuration);
        Assert.Equal(TimerConfig.MinDuration, config.BreakDuration);
    }

    // ---- FR-002: standalone break ---------------------------------------------------

    [Fact]
    public void FR002_StartBreak_RunsABreakWithoutAStudySession()
    {
        var (engine, _) = Build();

        engine.StartBreak(Config(study: 25, @break: 5));

        var state = engine.CurrentState;
        Assert.Equal(TimerMode.Break, state.Mode);
        Assert.Equal(TimeSpan.FromMinutes(5), state.RemainingTime);
        Assert.False(state.IsPaused);
    }

    [Fact]
    public void FR002_AStandaloneBreakEndsTheRunRatherThanStartingAStudySession()
    {
        var (engine, clock) = Build();
        SessionEndedEventArgs? ended = null;
        engine.SessionEnded += (_, e) => ended = e;

        engine.StartBreak(Config(@break: 1));
        clock.Run(TimeSpan.FromMinutes(1));

        Assert.NotNull(ended);
        Assert.True(ended!.RunCompleted);
        Assert.Equal(TimerMode.Idle, engine.CurrentState.Mode);
    }

    // ---- FR-005: skip ---------------------------------------------------------------

    [Fact]
    public void FR005_Skip_MovesFromStudyToBreakImmediately()
    {
        var (engine, clock) = Build();
        engine.Start(Config(study: 25, @break: 5));
        clock.Run(TimeSpan.FromMinutes(3));

        engine.Skip();

        var state = engine.CurrentState;
        Assert.Equal(TimerMode.Break, state.Mode);
        Assert.Equal(TimeSpan.FromMinutes(5), state.RemainingTime);
        Assert.False(state.IsPaused);
    }

    [Fact]
    public void FR005_Skip_IsFlaggedSoConsumersCanSuppressTheAlarm()
    {
        var (engine, _) = Build();
        SessionEndedEventArgs? ended = null;
        engine.SessionEnded += (_, e) => ended = e;

        engine.Start(Config());
        engine.Skip();

        Assert.NotNull(ended);
        Assert.True(ended!.WasSkipped);
    }

    [Fact]
    public void FR005_Skip_OverridesAutoStartBeingOff()
    {
        // Skipping is an explicit manual advance, so the next session should be running —
        // otherwise it would take a second click to get going.
        var (engine, _) = Build();
        var config = Config(study: 25, @break: 5);
        config.AutoStartBreak = false;

        engine.Start(config);
        engine.Skip();

        Assert.False(engine.CurrentState.IsPaused);
    }

    [Fact]
    public void FR005_Skip_WhileIdleDoesNothing()
    {
        var (engine, _) = Build();

        engine.Skip();

        Assert.Equal(TimerMode.Idle, engine.CurrentState.Mode);
    }

    // ---- FR-006 / FR-007: infinite mode vs session count ----------------------------

    [Fact]
    public void FR006_InfiniteMode_KeepsCyclingPastTheSessionCount()
    {
        var (engine, clock) = Build();
        var config = Config(study: 1, @break: 1);
        config.InfiniteMode = true;
        config.SessionCount = 2;

        engine.Start(config);

        // Four full study+break cycles — well past SessionCount.
        for (var i = 0; i < 8; i++)
        {
            clock.Run(TimeSpan.FromMinutes(1));
        }

        Assert.NotEqual(TimerMode.Idle, engine.CurrentState.Mode);
        Assert.Equal(5, engine.CurrentState.CurrentSession);
    }

    [Fact]
    public void FR007_FiniteRun_StopsAfterTheConfiguredNumberOfStudySessions()
    {
        var (engine, clock) = Build();
        var config = Config(study: 1, @break: 1);
        config.InfiniteMode = false;
        config.SessionCount = 2;

        SessionEndedEventArgs? last = null;
        engine.SessionEnded += (_, e) => last = e;

        engine.Start(config);
        clock.Run(TimeSpan.FromMinutes(1)); // study 1 -> break
        clock.Run(TimeSpan.FromMinutes(1)); // break -> study 2
        Assert.Equal(TimerMode.Study, engine.CurrentState.Mode);
        Assert.Equal(2, engine.CurrentState.CurrentSession);

        clock.Run(TimeSpan.FromMinutes(1)); // study 2 -> break
        clock.Run(TimeSpan.FromMinutes(1)); // break -> run over

        Assert.Equal(TimerMode.Idle, engine.CurrentState.Mode);
        Assert.NotNull(last);
        Assert.True(last!.RunCompleted);
    }

    [Fact]
    public void FR007_SessionCountIsClampedToOneThroughTen()
    {
        Assert.Equal(10, new TimerConfig { SessionCount = 99 }.Normalized().SessionCount);
        Assert.Equal(1, new TimerConfig { SessionCount = 0 }.Normalized().SessionCount);
    }

    // ---- FR-009: volume -------------------------------------------------------------

    [Fact]
    public void FR009_VolumeIsClampedToZeroThroughOneHundred()
    {
        Assert.Equal(100, new TimerConfig { AlarmVolume = 500 }.Normalized().AlarmVolume);
        Assert.Equal(0, new TimerConfig { AlarmVolume = -20 }.Normalized().AlarmVolume);
    }

    // ---- FR-013: restore ------------------------------------------------------------

    [Fact]
    public void FR013_Restore_ReinstatesModeRemainingTimeAndSessionNumber()
    {
        var (engine, _) = Build();

        engine.Restore(Config(), new SessionState
        {
            Mode = TimerMode.Study,
            RemainingTime = TimeSpan.FromMinutes(7),
            CurrentSession = 3
        });

        var state = engine.CurrentState;
        Assert.Equal(TimerMode.Study, state.Mode);
        Assert.Equal(TimeSpan.FromMinutes(7), state.RemainingTime);
        Assert.Equal(3, state.CurrentSession);
    }

    [Fact]
    public void FR013_Restore_ComesBackPausedSoNoTimeIsBurnedWhileTheAppWasClosed()
    {
        var (engine, clock) = Build();

        engine.Restore(Config(), new SessionState
        {
            Mode = TimerMode.Study,
            RemainingTime = TimeSpan.FromMinutes(7),
            CurrentSession = 1
        });

        Assert.True(engine.CurrentState.IsPaused);

        clock.Run(TimeSpan.FromMinutes(5));
        Assert.Equal(TimeSpan.FromMinutes(7), engine.CurrentState.RemainingTime);
    }

    [Fact]
    public void FR013_Restore_ResumesCleanlyFromWhereItLeftOff()
    {
        var (engine, clock) = Build();
        engine.Restore(Config(), new SessionState
        {
            Mode = TimerMode.Study,
            RemainingTime = TimeSpan.FromMinutes(7),
            CurrentSession = 1
        });

        engine.Resume();
        clock.Run(TimeSpan.FromMinutes(2));

        Assert.Equal(TimeSpan.FromMinutes(5), engine.CurrentState.RemainingTime);
    }

    [Fact]
    public void FR013_Restore_OfAnIdleStateIsANoOp()
    {
        var (engine, _) = Build();

        engine.Restore(Config(), new SessionState { Mode = TimerMode.Idle });

        Assert.Equal(TimerMode.Idle, engine.CurrentState.Mode);
    }
}
