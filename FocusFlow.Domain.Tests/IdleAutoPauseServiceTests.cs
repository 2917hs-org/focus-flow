using FocusFlow.Application.Interfaces;
using FocusFlow.Application.Services;
using FocusFlow.Domain.Engines;
using FocusFlow.Domain.Models;
using Microsoft.Extensions.Time.Testing;

namespace FocusFlow.Domain.Tests;

public class IdleAutoPauseServiceTests
{
    private sealed class FakeIdleTimeProvider : IIdleTimeProvider
    {
        public TimeSpan? IdleTime { get; set; }

        public TimeSpan? GetIdleTime() => IdleTime;
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public FakeSettingsService(TimerConfig config) => Current = config;

        public TimerConfig Current { get; set; }

        public event EventHandler<SettingsChangedEventArgs>? Changed;

        public void Update(Action<TimerConfig> mutate)
        {
            mutate(Current);
            Changed?.Invoke(this, new SettingsChangedEventArgs(Current));
        }

        public void Flush()
        {
        }
    }

    private static (
        ITimerService TimerService,
        FakeIdleTimeProvider IdleProvider,
        FakeSettingsService Settings,
        FakeTimeProvider Clock) Build(TimeSpan threshold, bool enabled = true)
    {
        var clock = new FakeTimeProvider();
        var timerService = new TimerService(new TimerEngine(clock));
        var settings = new FakeSettingsService(new TimerConfig
        {
            IdleAutoPauseEnabled = enabled,
            IdleAutoPauseThreshold = threshold
        });
        var idleProvider = new FakeIdleTimeProvider();

        // The service instance itself is discarded: once started it works entirely off
        // events and the injected clock/settings/idle-provider fakes, so the tests never
        // need to call back into it directly.
        var service = new IdleAutoPauseService(timerService, settings, idleProvider, clock);
        service.StartTracking();

        return (timerService, idleProvider, settings, clock);
    }

    [Fact]
    public async Task Poll_WhenIdleTimeReachesThreshold_PausesTheSession()
    {
        var (timerService, idleProvider, settings, clock) = Build(TimeSpan.FromMinutes(3));
        await timerService.StartAsync(settings.Current);

        idleProvider.IdleTime = TimeSpan.FromMinutes(3);
        clock.Run(TimeSpan.FromSeconds(30));

        Assert.True(timerService.CurrentState.IsPaused);
    }

    [Fact]
    public async Task Poll_WhenIdleTimeIsBelowThreshold_DoesNotPause()
    {
        var (timerService, idleProvider, settings, clock) = Build(TimeSpan.FromMinutes(3));
        await timerService.StartAsync(settings.Current);

        idleProvider.IdleTime = TimeSpan.FromMinutes(2);
        clock.Run(TimeSpan.FromSeconds(30));

        Assert.False(timerService.CurrentState.IsPaused);
    }

    [Fact]
    public async Task Poll_NeverAutoResumesOnceActivityReturns()
    {
        var (timerService, idleProvider, settings, clock) = Build(TimeSpan.FromMinutes(3));
        await timerService.StartAsync(settings.Current);

        idleProvider.IdleTime = TimeSpan.FromMinutes(5);
        clock.Run(TimeSpan.FromSeconds(30));
        Assert.True(timerService.CurrentState.IsPaused);

        // Activity returns and stays present for several more polls — the session must
        // stay paused until the user explicitly resumes it.
        idleProvider.IdleTime = TimeSpan.Zero;
        clock.Run(TimeSpan.FromMinutes(5));

        Assert.True(timerService.CurrentState.IsPaused);
    }

    [Fact]
    public async Task Poll_WhenAutoPauseIsDisabled_DoesNotPause()
    {
        var (timerService, idleProvider, settings, clock) = Build(TimeSpan.FromMinutes(3), enabled: false);
        await timerService.StartAsync(settings.Current);

        idleProvider.IdleTime = TimeSpan.FromMinutes(10);
        clock.Run(TimeSpan.FromSeconds(30));

        Assert.False(timerService.CurrentState.IsPaused);
    }

    [Fact]
    public async Task Poll_WhenThePlatformCannotReportIdleTime_DoesNotPause()
    {
        var (timerService, idleProvider, settings, clock) = Build(TimeSpan.FromMinutes(3));
        await timerService.StartAsync(settings.Current);

        idleProvider.IdleTime = null;
        clock.Run(TimeSpan.FromSeconds(30));

        Assert.False(timerService.CurrentState.IsPaused);
    }

    [Fact]
    public void Poll_WhileNoSessionIsRunning_NeverPolls()
    {
        var (timerService, idleProvider, _, clock) = Build(TimeSpan.FromMinutes(3));

        idleProvider.IdleTime = TimeSpan.FromMinutes(10);
        clock.Run(TimeSpan.FromMinutes(5));

        Assert.Equal(TimerMode.Idle, timerService.CurrentState.Mode);
    }
}
