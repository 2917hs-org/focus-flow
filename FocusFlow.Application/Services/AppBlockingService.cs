using FocusFlow.Application.Interfaces;
using FocusFlow.Domain.Models;
using FocusFlow.Domain.Services;

namespace FocusFlow.Application.Services;

/// <summary>
/// Enforces the blocked-apps list while a session is running and unpaused.
/// </summary>
/// <remarks>
/// Modeled on <see cref="SessionHistoryService"/>: constructor-injected, started once via
/// <see cref="StartTracking"/>, subscribes to <see cref="ITimerService.TimerUpdated"/>. The
/// monitor is only told to watch while a session is actually running — polling the
/// frontmost app outside a session would just burn battery for nothing.
/// </remarks>
public sealed class AppBlockingService : IAppBlockingService, IDisposable
{
    private readonly ITimerService _timerService;
    private readonly ISettingsService _settings;
    private readonly IAppBlockingMonitor _monitor;
    private readonly IAppLogger? _logger;

    private bool _started;
    private bool _disposed;
    private bool _watching;

    public AppBlockingService(
        ITimerService timerService,
        ISettingsService settings,
        IAppBlockingMonitor monitor,
        IAppLogger? logger = null)
    {
        _timerService = timerService;
        _settings = settings;
        _monitor = monitor;
        _logger = logger;
    }

    public bool IsSupported => _monitor.IsSupported;

    public IReadOnlyList<AppInfo> GetInstalledApplications() => _monitor.GetInstalledApplications();

    public void RequestAccessibilityAccess() => _monitor.RequestAccessibilityAccess();

    public string? GetFrontmostBundleId() => _monitor.GetFrontmostBundleId();

    public AppInfo? GetFrontmostApp() => _monitor.GetFrontmostApp();

    public void StartTracking()
    {
        if (_started || !_monitor.IsSupported)
        {
            return;
        }

        _started = true;
        _monitor.AppActivated += OnAppActivated;
        _timerService.TimerUpdated += OnTimerUpdated;

        UpdateWatchState(_timerService.CurrentState);
    }

    private void OnTimerUpdated(object? sender, TimerUpdatedEventArgs e) => UpdateWatchState(e.State);

    private void UpdateWatchState(SessionState state)
    {
        if (state.IsRunning == _watching)
        {
            return;
        }

        _watching = state.IsRunning;

        if (_watching)
        {
            _monitor.StartWatching();
        }
        else
        {
            _monitor.StopWatching();
        }
    }

    private void OnAppActivated(object? sender, string bundleId)
    {
        if (_disposed)
        {
            return;
        }

        if (!AppBlockPolicy.ShouldIntervene(_timerService.CurrentState, _settings.Current.BlockedAppIds, bundleId))
        {
            return;
        }

        try
        {
            _monitor.Intervene(bundleId);
        }
        catch (Exception e)
        {
            // Best effort — a failed intervention should never take the session down.
            _logger?.Warn($"App block intervention for {bundleId} failed: {e.Message}");
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
            _monitor.AppActivated -= OnAppActivated;
            _timerService.TimerUpdated -= OnTimerUpdated;
        }

        _monitor.StopWatching();
    }
}
