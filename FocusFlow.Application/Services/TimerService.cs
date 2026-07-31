using FocusFlow.Application.Interfaces;
using FocusFlow.Domain.Interfaces;
using FocusFlow.Domain.Models;

namespace FocusFlow.Application.Services;

/// <summary>
/// Thin application-layer facade over <see cref="ITimerEngine"/>, forwarding the engine's
/// per-second tick so the UI and tray can follow the countdown.
/// </summary>
public sealed class TimerService : ITimerService, IDisposable
{
    private readonly ITimerEngine _timerEngine;

    public TimerService(ITimerEngine timerEngine)
    {
        _timerEngine = timerEngine;
        _timerEngine.Tick += OnTick;
        _timerEngine.SessionEnded += OnSessionEnded;
        _timerEngine.SystemResumed += OnSystemResumed;
    }

    public SessionState CurrentState => _timerEngine.CurrentState;

    public event EventHandler<TimerUpdatedEventArgs>? TimerUpdated;
    public event EventHandler<SessionEndedEventArgs>? SessionEnded;
    public event EventHandler<SystemResumedEventArgs>? SystemResumed;

    public Task StartAsync(TimerConfig config)
    {
        _timerEngine.Start(config);
        return Task.CompletedTask;
    }

    public Task StartBreakAsync(TimerConfig config)
    {
        _timerEngine.StartBreak(config);
        return Task.CompletedTask;
    }

    public void Pause() => _timerEngine.Pause();

    public void Resume() => _timerEngine.Resume();

    public void Stop() => _timerEngine.Stop();

    public void Reset() => _timerEngine.Reset();

    public void Skip() => _timerEngine.Skip();

    public void Restore(TimerConfig config, SessionState state) => _timerEngine.Restore(config, state);

    private void OnTick(object? sender, TimerTickEventArgs e) =>
        TimerUpdated?.Invoke(this, new TimerUpdatedEventArgs(e.State));

    private void OnSessionEnded(object? sender, SessionEndedEventArgs e) =>
        SessionEnded?.Invoke(this, e);

    private void OnSystemResumed(object? sender, SystemResumedEventArgs e) =>
        SystemResumed?.Invoke(this, e);

    public void Dispose()
    {
        _timerEngine.Tick -= OnTick;
        _timerEngine.SessionEnded -= OnSessionEnded;
        _timerEngine.SystemResumed -= OnSystemResumed;
    }
}
