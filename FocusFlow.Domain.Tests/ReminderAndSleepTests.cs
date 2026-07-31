using FocusFlow.Domain.Engines;
using FocusFlow.Domain.Interfaces;
using FocusFlow.Domain.Models;
using Microsoft.Extensions.Time.Testing;

namespace FocusFlow.Domain.Tests;

/// <summary>
/// The pre-end reminder (which replaces the always-visible tray countdown) and the
/// "session slept through" warning.
/// </summary>
public class ReminderAndSleepTests
{
    private static (TimerEngine Engine, FakeTimeProvider Clock) Build()
    {
        var clock = new FakeTimeProvider();
        return (new TimerEngine(clock), clock);
    }

    private static TimerConfig Config(int study = 25, int leadMinutes = 2) => new()
    {
        StudyDuration = TimeSpan.FromMinutes(study),
        BreakDuration = TimeSpan.FromMinutes(5),
        ReminderEnabled = true,
        ReminderLeadTime = TimeSpan.FromMinutes(leadMinutes)
    };

    [Fact]
    public void ReminderFiresAtTheConfiguredLeadTime()
    {
        var (engine, clock) = Build();
        ReminderDueEventArgs? due = null;
        engine.ReminderDue += (_, e) => due = e;

        engine.Start(Config(study: 10, leadMinutes: 2));

        clock.Run(TimeSpan.FromMinutes(7));
        Assert.Null(due);

        clock.Run(TimeSpan.FromMinutes(1));
        Assert.NotNull(due);
        Assert.Equal(TimerMode.Study, due!.Mode);
        Assert.True(due.Remaining <= TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void ReminderFiresOnlyOncePerSession()
    {
        var (engine, clock) = Build();
        var count = 0;
        engine.ReminderDue += (_, _) => count++;

        engine.Start(Config(study: 5, leadMinutes: 2));
        clock.Run(TimeSpan.FromMinutes(4));

        Assert.Equal(1, count);
    }

    [Fact]
    public void EachSessionGetsItsOwnReminder()
    {
        var (engine, clock) = Build();
        var count = 0;
        engine.ReminderDue += (_, _) => count++;

        var config = Config(study: 3, leadMinutes: 1);
        config.BreakDuration = TimeSpan.FromMinutes(3);

        engine.Start(config);
        clock.Run(TimeSpan.FromMinutes(3)); // study done -> break
        clock.Run(TimeSpan.FromMinutes(3)); // break done -> study

        Assert.Equal(2, count);
    }

    [Fact]
    public void ReminderIsSkippedWhenDisabled()
    {
        var (engine, clock) = Build();
        var count = 0;
        engine.ReminderDue += (_, _) => count++;

        var config = Config(study: 5, leadMinutes: 2);
        config.ReminderEnabled = false;

        engine.Start(config);
        clock.Run(TimeSpan.FromMinutes(4));

        Assert.Equal(0, count);
    }

    [Fact]
    public void ALeadLongerThanTheSessionDoesNotFireImmediately()
    {
        // A 10-minute lead on a 5-minute session would otherwise nag the moment it starts.
        var (engine, clock) = Build();
        var count = 0;
        engine.ReminderDue += (_, _) => count++;

        engine.Start(Config(study: 5, leadMinutes: 10));
        clock.Run(TimeSpan.FromMinutes(1));

        Assert.Equal(0, count);
    }

    [Fact]
    public void ReminderLeadIsClampedToAUsableRange()
    {
        Assert.Equal(
            TimerConfig.MaxReminderLead,
            new TimerConfig { ReminderLeadTime = TimeSpan.FromHours(3) }.Normalized().ReminderLeadTime);
        Assert.Equal(
            TimerConfig.MinReminderLead,
            new TimerConfig { ReminderLeadTime = TimeSpan.Zero }.Normalized().ReminderLeadTime);
    }

    [Fact]
    public void SleepingPastTheEndFlagsTheSessionAsInterrupted()
    {
        var (engine, clock) = Build();
        SystemResumedEventArgs? resumed = null;
        engine.SystemResumed += (_, e) => resumed = e;

        engine.Start(Config(study: 25));
        clock.Run(TimeSpan.FromMinutes(5));

        clock.Advance(TimeSpan.FromHours(3)); // asleep far past the end

        Assert.NotNull(resumed);
        Assert.True(resumed!.SessionWouldHaveEnded);
    }

    [Fact]
    public void AShortNapDoesNotFlagTheSessionAsInterrupted()
    {
        var (engine, clock) = Build();
        SystemResumedEventArgs? resumed = null;
        engine.SystemResumed += (_, e) => resumed = e;

        engine.Start(Config(study: 25));
        clock.Run(TimeSpan.FromMinutes(1));

        clock.Advance(TimeSpan.FromMinutes(2)); // well within the remaining 24 minutes

        Assert.NotNull(resumed);
        Assert.False(resumed!.SessionWouldHaveEnded);
    }

    [Fact]
    public void ASleptThroughSessionIsStillNotCompleted()
    {
        // The whole point: time asleep is not focus time, so nothing is credited and the
        // session is left where the user abandoned it.
        var (engine, clock) = Build();
        var ends = 0;
        engine.SessionEnded += (_, _) => ends++;

        engine.Start(Config(study: 25));
        clock.Run(TimeSpan.FromMinutes(5));
        var beforeSleep = engine.CurrentState.RemainingTime;

        clock.Advance(TimeSpan.FromHours(3));

        Assert.Equal(0, ends);
        Assert.Equal(TimerMode.Study, engine.CurrentState.Mode);
        Assert.Equal(beforeSleep, engine.CurrentState.RemainingTime);
    }

    [Fact]
    public void NoLateReminderAfterSleepingPastTheEnd()
    {
        // The reminder would otherwise fire on wake, announcing an ending that already
        // came and went while the machine was shut.
        var (engine, clock) = Build();
        var reminders = 0;
        engine.ReminderDue += (_, _) => reminders++;

        engine.Start(Config(study: 25, leadMinutes: 2));
        clock.Run(TimeSpan.FromMinutes(5));

        clock.Advance(TimeSpan.FromHours(3));
        clock.Run(TimeSpan.FromMinutes(1));

        Assert.Equal(0, reminders);
    }
}
