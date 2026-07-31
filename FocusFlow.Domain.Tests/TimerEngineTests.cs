using FocusFlow.Domain.Engines;
using FocusFlow.Domain.Interfaces;
using FocusFlow.Domain.Models;
using Microsoft.Extensions.Time.Testing;

namespace FocusFlow.Domain.Tests;

/// <summary>
/// Drives <see cref="TimerEngine"/> with a fake clock, so the countdown is asserted
/// deterministically instead of by sleeping.
/// </summary>
public class TimerEngineTests
{
    private static TimerConfig Config(int studyMinutes = 25, int breakMinutes = 5) => new()
    {
        StudyDuration = TimeSpan.FromMinutes(studyMinutes),
        BreakDuration = TimeSpan.FromMinutes(breakMinutes)
    };

    private static (TimerEngine Engine, FakeTimeProvider Clock) Build()
    {
        var clock = new FakeTimeProvider();
        return (new TimerEngine(clock), clock);
    }

    [Fact]
    public void Start_BeginsStudySessionAtFullDuration()
    {
        var (engine, _) = Build();

        engine.Start(Config());

        var state = engine.CurrentState;
        Assert.Equal(TimerMode.Study, state.Mode);
        Assert.Equal(TimeSpan.FromMinutes(25), state.RemainingTime);
        Assert.Equal(1, state.CurrentSession);
        Assert.False(state.IsPaused);
    }

    [Fact]
    public void CountsDownAsTimeAdvances()
    {
        var (engine, clock) = Build();
        engine.Start(Config());

        clock.Run(TimeSpan.FromSeconds(90));

        Assert.Equal(TimeSpan.FromMinutes(25) - TimeSpan.FromSeconds(90), engine.CurrentState.RemainingTime);
    }

    [Fact]
    public void RaisesOneTickPerSecond_NotPerPoll()
    {
        var (engine, clock) = Build();
        var ticks = 0;
        engine.Tick += (_, _) => ticks++;

        engine.Start(Config());
        ticks = 0; // ignore the immediate tick Start raises

        // Stepped a second at a time on purpose. FakeTimeProvider.Advance moves the clock
        // to the end of the interval *before* firing due callbacks, so a single
        // Advance(5s) would run the 200ms poll 25 times with all of them observing the
        // same instant — which cannot exercise per-second behaviour at all.
        for (var i = 0; i < 5; i++)
        {
            clock.Run(TimeSpan.FromSeconds(1));
        }

        // 25 poll callbacks collapsed into 5 UI-visible ticks.
        Assert.Equal(5, ticks);
    }

    [Fact]
    public void Pause_FreezesRemainingTime()
    {
        var (engine, clock) = Build();
        engine.Start(Config());
        clock.Run(TimeSpan.FromSeconds(30));

        engine.Pause();
        clock.Run(TimeSpan.FromMinutes(10));

        var state = engine.CurrentState;
        Assert.True(state.IsPaused);
        Assert.Equal(TimeSpan.FromMinutes(25) - TimeSpan.FromSeconds(30), state.RemainingTime);
    }

    [Fact]
    public void PauseResume_IsLossless()
    {
        var (engine, clock) = Build();
        engine.Start(Config());

        clock.Run(TimeSpan.FromSeconds(30));
        engine.Pause();
        clock.Run(TimeSpan.FromHours(1));
        engine.Resume();
        clock.Run(TimeSpan.FromSeconds(30));

        // 60s of counted time total, regardless of how long it sat paused.
        Assert.Equal(TimeSpan.FromMinutes(24), engine.CurrentState.RemainingTime);
    }

    [Fact]
    public void Resume_OnARunningTimer_IsIgnored()
    {
        var (engine, clock) = Build();
        engine.Start(Config());
        clock.Run(TimeSpan.FromSeconds(10));

        engine.Resume();
        clock.Run(TimeSpan.FromSeconds(10));

        Assert.Equal(TimeSpan.FromMinutes(25) - TimeSpan.FromSeconds(20), engine.CurrentState.RemainingTime);
    }

    [Fact]
    public void StudyEnd_RaisesSessionEndedAndSwitchesToBreak()
    {
        var (engine, clock) = Build();
        SessionEndedEventArgs? ended = null;
        engine.SessionEnded += (_, e) => ended = e;

        engine.Start(Config(studyMinutes: 1));
        clock.Run(TimeSpan.FromMinutes(1));

        Assert.NotNull(ended);
        Assert.Equal(TimerMode.Study, ended!.CompletedMode);
        Assert.Equal(TimerMode.Break, ended.NextMode);
        Assert.Equal(TimerMode.Break, engine.CurrentState.Mode);
    }

    [Fact]
    public void AutoStartBreak_ContinuesCountingIntoTheBreak()
    {
        var (engine, clock) = Build();
        var config = Config(studyMinutes: 1, breakMinutes: 5);
        config.AutoStartBreak = true;

        engine.Start(config);
        clock.Run(TimeSpan.FromMinutes(1));   // finish study
        clock.Run(TimeSpan.FromSeconds(30));  // into the break

        var state = engine.CurrentState;
        Assert.Equal(TimerMode.Break, state.Mode);
        Assert.False(state.IsPaused);
        Assert.Equal(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(30), state.RemainingTime);
    }

    [Fact]
    public void AutoStartBreakDisabled_WaitsPausedAtFullBreakDuration()
    {
        var (engine, clock) = Build();
        var config = Config(studyMinutes: 1, breakMinutes: 5);
        config.AutoStartBreak = false;

        engine.Start(config);
        clock.Run(TimeSpan.FromMinutes(1));
        clock.Run(TimeSpan.FromMinutes(10)); // should not consume the break

        var state = engine.CurrentState;
        Assert.Equal(TimerMode.Break, state.Mode);
        Assert.True(state.IsPaused);
        Assert.Equal(TimeSpan.FromMinutes(5), state.RemainingTime);
    }

    [Fact]
    public void AutoStartStudyDisabled_WaitsPausedAfterTheBreak()
    {
        var (engine, clock) = Build();
        var config = Config(studyMinutes: 1, breakMinutes: 1);
        config.AutoStartBreak = true;
        config.AutoStartStudy = false;

        engine.Start(config);
        clock.Run(TimeSpan.FromMinutes(1)); // study -> break
        clock.Run(TimeSpan.FromMinutes(1)); // break -> study, should pause
        clock.Run(TimeSpan.FromMinutes(5));

        var state = engine.CurrentState;
        Assert.Equal(TimerMode.Study, state.Mode);
        Assert.True(state.IsPaused);
        Assert.Equal(TimeSpan.FromMinutes(1), state.RemainingTime);
    }

    [Fact]
    public void SessionCounter_IncrementsOnEachReturnToStudy()
    {
        var (engine, clock) = Build();
        var config = Config(studyMinutes: 1, breakMinutes: 1);

        engine.Start(config);
        Assert.Equal(1, engine.CurrentState.CurrentSession);

        clock.Run(TimeSpan.FromMinutes(1)); // -> break
        Assert.Equal(1, engine.CurrentState.CurrentSession);

        clock.Run(TimeSpan.FromMinutes(1)); // -> study #2
        Assert.Equal(2, engine.CurrentState.CurrentSession);
    }

    [Fact]
    public void Reset_RestoresTheCurrentSessionsDuration_NotTheBreaks()
    {
        // Regression: Reset() used to call Stop() first, which set Mode to Idle, so the
        // "am I in a study session?" check always failed and it reset to BreakDuration.
        var (engine, clock) = Build();
        engine.Start(Config(studyMinutes: 25, breakMinutes: 5));
        clock.Run(TimeSpan.FromMinutes(10));

        engine.Reset();

        var state = engine.CurrentState;
        Assert.Equal(TimerMode.Study, state.Mode);
        Assert.Equal(TimeSpan.FromMinutes(25), state.RemainingTime);
        Assert.True(state.IsPaused);
    }

    [Fact]
    public void Reset_DuringABreak_RestoresTheBreakDuration()
    {
        var (engine, clock) = Build();
        engine.Start(Config(studyMinutes: 1, breakMinutes: 5));
        clock.Run(TimeSpan.FromMinutes(1));   // into the break
        clock.Run(TimeSpan.FromSeconds(30));

        engine.Reset();

        var state = engine.CurrentState;
        Assert.Equal(TimerMode.Break, state.Mode);
        Assert.Equal(TimeSpan.FromMinutes(5), state.RemainingTime);
    }

    [Fact]
    public void Stop_ReturnsToIdleAndResetsTheSessionCount()
    {
        var (engine, clock) = Build();
        engine.Start(Config(studyMinutes: 1, breakMinutes: 1));
        clock.Run(TimeSpan.FromMinutes(2)); // run into session 2

        engine.Stop();

        var state = engine.CurrentState;
        Assert.Equal(TimerMode.Idle, state.Mode);
        Assert.Equal(1, state.CurrentSession);
        Assert.False(state.IsRunning);
    }

    [Fact]
    public void ALateTimerCallbackDoesNotCauseDrift()
    {
        // A callback that is late but not suspend-late (under the 5s threshold): the
        // remaining time is recomputed from the segment start, never accumulated, so the
        // delay is absorbed rather than lost. A larger gap is deliberately treated as
        // sleep instead — see ResilienceTests.
        var (engine, clock) = Build();
        engine.Start(Config(studyMinutes: 25));

        clock.Run(TimeSpan.FromMinutes(1));
        clock.Advance(TimeSpan.FromSeconds(4));

        Assert.Equal(TimeSpan.FromMinutes(25) - TimeSpan.FromSeconds(64), engine.CurrentState.RemainingTime);
    }

    [Fact]
    public void Start_WhileRunning_RestartsRatherThanThrowing()
    {
        var (engine, clock) = Build();
        engine.Start(Config(studyMinutes: 25));
        clock.Run(TimeSpan.FromMinutes(5));

        var exception = Record.Exception(() => engine.Start(Config(studyMinutes: 10)));

        Assert.Null(exception);
        Assert.Equal(TimeSpan.FromMinutes(10), engine.CurrentState.RemainingTime);
    }
}
