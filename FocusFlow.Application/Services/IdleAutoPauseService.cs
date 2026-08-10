using FocusFlow.Application.Interfaces;
using FocusFlow.Domain.Models;
using FocusFlow.Domain.Services;

namespace FocusFlow.Application.Services;

/// <summary>
/// Automatically pauses the active session once there has been no keyboard/mouse input
/// for longer than the configured threshold.
/// </summary>
/// <remarks>
/// Modeled on <see cref="AppBlockingService"/>: constructor-injected, started once via
/// <see cref="StartTracking"/>, watching <see cref="ITimerService.TimerUpdated"/> to know
/// when a session is actually running so the idle query only runs while there is
/// something it could usefully pause. Unlike AppBlockingService there is no OS-level
/// notification to subscribe to for "user went idle", so this polls instead — every 30
/// seconds is frequent enough to catch someone stepping away within a reasonable margin
/// of the threshold without it being worth waking the process any more often than that.
///
/// Deliberately never resumes anything: this only ever calls <see cref="ITimerService.Pause"/>.
/// Picking the session back up is left entirely to the user, since activity returning
/// (e.g. the mouse moving because someone walked back to the desk) is not evidence they
/// are back to focusing.
/// </remarks>
public sealed class IdleAutoPauseService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly ITimerService _timerService;
    private readonly ISettingsService _settings;
    private readonly IIdleTimeProvider _idleTimeProvider;
    private readonly ITimer _timer;

    private bool _started;
    private bool _disposed;
    private bool _polling;

    public IdleAutoPauseService(
        ITimerService timerService, ISettingsService settings, IIdleTimeProvider idleTimeProvider)
        : this(timerService, settings, idleTimeProvider, TimeProvider.System)
    {
    }

    public IdleAutoPauseService(
        ITimerService timerService,
        ISettingsService settings,
        IIdleTimeProvider idleTimeProvider,
        TimeProvider timeProvider)
    {
        _timerService = timerService;
        _settings = settings;
        _idleTimeProvider = idleTimeProvider;
        _timer = timeProvider.CreateTimer(
            _ => Poll(),
            state: null,
            dueTime: Timeout.InfiniteTimeSpan,
            period: Timeout.InfiniteTimeSpan);
    }

    public void StartTracking()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _timerService.TimerUpdated += OnTimerUpdated;
        UpdatePollState(_timerService.CurrentState);
    }

    private void OnTimerUpdated(object? sender, TimerUpdatedEventArgs e) => UpdatePollState(e.State);

    private void UpdatePollState(SessionState state)
    {
        // TimerUpdated fires once a second while a session runs, not just on
        // start/pause/stop transitions — re-arming the timer on every one of those ticks
        // would push its due time back by another 30s each time and it would never
        // actually fire. Gate on the edge, same as AppBlockingService.UpdateWatchState.
        if (state.IsRunning == _polling)
        {
            return;
        }

        _polling = state.IsRunning;

        // Parking the timer while nothing is running avoids waking the process every 30s
        // for no reason — same reasoning as AppBlockingService only watching while active.
        var interval = _polling ? PollInterval : Timeout.InfiniteTimeSpan;
        _timer.Change(interval, interval);
    }

    private void Poll()
    {
        if (_disposed)
        {
            return;
        }

        var config = _settings.Current;
        if (!config.IdleAutoPauseEnabled)
        {
            return;
        }

        var idleTime = _idleTimeProvider.GetIdleTime();
        if (idleTime is null)
        {
            return;
        }

        if (IdleAutoPausePolicy.ShouldPause(_timerService.CurrentState, idleTime.Value, config.IdleAutoPauseThreshold))
        {
            _timerService.Pause();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_started)
        {
            _timerService.TimerUpdated -= OnTimerUpdated;
        }

        _timer.Dispose();
    }
}
