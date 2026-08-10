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

/// <summary>Total study minutes logged on one local calendar day.</summary>
public sealed record DailyFocusMinutes(DateOnly Day, double Minutes);

/// <summary>
/// Total study time under one <see cref="SessionRecord.Label"/>. Null groups
/// every session left unlabelled, so a range's label totals still add up to
/// <see cref="HistorySummary.TotalStudyTime"/> rather than silently dropping sessions.
/// </summary>
public sealed record LabelTotal(string? Label, TimeSpan TotalTime, int SessionCount);

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

    /// <summary>
    /// The individual records behind <see cref="Summarise"/>, newest first — this is the
    /// "later" the class doc comment refers to: a reporting view reading them back rather
    /// than just totalling them.
    /// </summary>
    public IReadOnlyList<SessionRecord> GetRecords(DateTimeOffset? since = null)
    {
        try
        {
            return _store.Read(since).OrderByDescending(r => r.EndedAt).ToList();
        }
        catch (Exception e)
        {
            // Same reasoning as Summarise: an unreadable log degrades to "no history
            // shown" rather than an exception reaching the view model.
            _logger?.Warn($"Reading session history failed: {e.Message}");
            return [];
        }
    }

    /// <summary>
    /// Below this, a study session doesn't count toward the streak even though it was
    /// worth logging — an instant Stop is a change of mind, not a day of showing up.
    /// Deliberately not restricted to <see cref="SessionOutcome.Completed"/>: most real
    /// sessions in this log are Stopped or Skipped rather than run to zero, and a streak
    /// that only recognises finished sessions would go dark on days someone genuinely
    /// used the app.
    /// </summary>
    private static readonly TimeSpan MinimumStreakSessionDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Consecutive local days, up to and including today, with at least one study session
    /// of at least <see cref="MinimumStreakSessionDuration"/> — any outcome. A day with
    /// nothing logged yet doesn't break the streak until the day before it is also empty
    /// — today isn't over yet, so yesterday's session still counts until a full day
    /// passes with no session at all.
    /// </summary>
    public int CurrentStreak()
    {
        try
        {
            var activeDays = _store.Read()
                .Where(r => r.Mode == TimerMode.Study && r.ActualDuration >= MinimumStreakSessionDuration)
                .Select(r => LocalDay(r.EndedAt))
                .ToHashSet();

            if (activeDays.Count == 0)
            {
                return 0;
            }

            var today = LocalDay(_timeProvider.GetUtcNow());
            var cursor = activeDays.Contains(today) ? today : today.AddDays(-1);

            var streak = 0;
            while (activeDays.Contains(cursor))
            {
                streak++;
                cursor = cursor.AddDays(-1);
            }

            return streak;
        }
        catch (Exception e)
        {
            // Same reasoning as Summarise/GetRecords: an unreadable log degrades to "no
            // streak" rather than an exception reaching the view model.
            _logger?.Warn($"Reading session history failed: {e.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Total study minutes per local day since <paramref name="since"/>, oldest first —
    /// the data behind a daily bar chart. Only days with at least one study session
    /// appear; days with none are the caller's to fill in as zero.
    /// </summary>
    public IReadOnlyList<DailyFocusMinutes> DailyFocusMinutesSince(DateTimeOffset? since = null)
    {
        try
        {
            return _store.Read(since)
                .Where(r => r.Mode == TimerMode.Study)
                .GroupBy(r => LocalDay(r.EndedAt))
                .Select(g => new DailyFocusMinutes(g.Key, g.Sum(r => r.ActualDuration.TotalMinutes)))
                .OrderBy(d => d.Day)
                .ToList();
        }
        catch (Exception e)
        {
            _logger?.Warn($"Reading session history failed: {e.Message}");
            return [];
        }
    }

    /// <summary>
    /// Total study time and session count per label, since <paramref name="since"/>,
    /// busiest label first. Study only — mirrors <see cref="Summarise"/>'s "focused time"
    /// framing rather than folding in the paired break minutes a labelled run also carries.
    /// </summary>
    /// <remarks>
    /// Grouped case-insensitively, matching how <c>HistoryViewModel</c>'s label filter
    /// matches records — "Thesis" and "thesis" are the same label everywhere, not one
    /// bucket here and two entries in the filtered list. The group's key keeps whichever
    /// casing was typed first; it's a display label, not an identity.
    /// </remarks>
    public IReadOnlyList<LabelTotal> LabelTotalsSince(DateTimeOffset? since = null)
    {
        try
        {
            return _store.Read(since)
                .Where(r => r.Mode == TimerMode.Study)
                .GroupBy(
                    r => string.IsNullOrWhiteSpace(r.Label) ? null : r.Label,
                    StringComparer.OrdinalIgnoreCase)
                .Select(g => new LabelTotal(
                    g.Key,
                    g.Aggregate(TimeSpan.Zero, (sum, r) => sum + r.ActualDuration),
                    g.Count()))
                .OrderByDescending(t => t.TotalTime)
                .ToList();
        }
        catch (Exception e)
        {
            _logger?.Warn($"Reading session history failed: {e.Message}");
            return [];
        }
    }

    /// <summary>
    /// Which local calendar day a UTC instant falls on, using the injected
    /// <see cref="TimeProvider"/>'s local zone rather than the host's, so streak and
    /// chart bucketing stay deterministic under <c>FakeTimeProvider</c> in tests.
    /// </summary>
    private DateOnly LocalDay(DateTimeOffset utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, _timeProvider.LocalTimeZone).Date);

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
