using FocusFlow.Application.Interfaces;
using FocusFlow.Domain.Interfaces;
using FocusFlow.Domain.Models;

namespace FocusFlow.Application.Services;

/// <summary>Aggregate over a set of session records, for reporting.</summary>
public sealed record HistorySummary(
    int CompletedStudySessions,
    TimeSpan TotalStudyTime,
    TimeSpan TotalBreakTime)
{
    public static readonly HistorySummary Empty = new(0, TimeSpan.Zero, TimeSpan.Zero);
}

/// <summary>
/// Records finished sessions to the local history log.
/// </summary>
/// <remarks>
/// The log is the durable artefact this version delivers; reporting is expected to be
/// built on top of <see cref="ISessionHistoryStore.Read"/> later, which is why the records
/// carry raw planned/actual durations rather than anything pre-aggregated.
/// </remarks>
public sealed class SessionHistoryService : IDisposable
{
    /// <summary>
    /// Sessions shorter than this are not logged — starting and immediately stopping is a
    /// misclick, not something anyone wants in their productivity history.
    /// </summary>
    private static readonly TimeSpan MinimumLoggedDuration = TimeSpan.FromSeconds(1);

    private readonly ITimerService _timerService;
    private readonly ISessionHistoryStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly IUserAlerts? _alerts;
    private readonly IAppLogger? _logger;

    private bool _started;
    private bool _disposed;

    public SessionHistoryService(
        ITimerService timerService,
        ISessionHistoryStore store,
        TimeProvider timeProvider,
        IUserAlerts? alerts = null,
        IAppLogger? logger = null)
    {
        _timerService = timerService;
        _store = store;
        _timeProvider = timeProvider;
        _alerts = alerts;
        _logger = logger;
    }

    public void StartTracking()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _timerService.SessionEnded += OnSessionEnded;
    }

    private void OnSessionEnded(object? sender, SessionEndedEventArgs e)
    {
        if (_disposed || e.ActualDuration < MinimumLoggedDuration)
        {
            return;
        }

        try
        {
            _store.Append(e.ToRecord(_timeProvider.GetUtcNow()));
        }
        catch (Exception failure)
        {
            // Losing one line must never interrupt the session that just ended, but a
            // history that silently stops recording is worse than one that says so.
            _alerts?.Report(
                "history-save",
                "FocusFlow can't record your session history",
                "Completed sessions aren't being logged, so your totals will be "
                + "incomplete.\n\n" + failure.Message);
        }
    }

    /// <summary>Summarises records since <paramref name="since"/> (null = all time).</summary>
    public HistorySummary Summarise(DateTimeOffset? since = null)
    {
        try
        {
            var records = _store.Read(since);

            return new HistorySummary(
                records.Count(r => r.Mode == TimerMode.Study && r.Outcome == SessionOutcome.Completed),
                Total(records, TimerMode.Study),
                Total(records, TimerMode.Break));
        }
        catch (Exception e)
        {
            // The "today" summary just goes blank rather than throwing through to the
            // view model — but a history that stopped being readable is worth knowing
            // about even if it isn't worth interrupting anyone for.
            _logger?.Warn($"Reading session history failed: {e.Message}");
            return HistorySummary.Empty;
        }
    }

    private static TimeSpan Total(IEnumerable<SessionRecord> records, TimerMode mode) =>
        records.Where(r => r.Mode == mode)
            .Aggregate(TimeSpan.Zero, (sum, r) => sum + r.ActualDuration);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timerService.SessionEnded -= OnSessionEnded;
    }
}
