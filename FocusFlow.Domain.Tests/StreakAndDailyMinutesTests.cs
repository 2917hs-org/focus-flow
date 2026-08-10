using FocusFlow.Application.Interfaces;
using FocusFlow.Application.Services;
using FocusFlow.Domain.Engines;
using FocusFlow.Domain.Models;
using FocusFlow.Infrastructure.Storage;
using Microsoft.Extensions.Time.Testing;

namespace FocusFlow.Domain.Tests;

/// <summary>
/// Streak and daily-minutes reporting, both built as pure computations over
/// <see cref="ISessionHistoryStore.Read"/> the same way <see cref="SessionHistoryTests"/>
/// covers Summarise/GetRecords — kept in their own file since the day-boundary and
/// timezone edge cases here don't share much with that file's append/read scenarios.
/// </summary>
public class StreakAndDailyMinutesTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "focusflow-streak-tests", Guid.NewGuid().ToString("N"));

    private string HistoryPath => Path.Combine(_directory, "history.jsonl");

    private static SessionRecord Record(
        TimerMode mode,
        int minutes,
        SessionOutcome outcome = SessionOutcome.Completed,
        int daysAgo = 0) => new()
    {
        Mode = mode,
        Outcome = outcome,
        StartedAt = DateTimeOffset.UtcNow.AddDays(-daysAgo).AddMinutes(-minutes),
        EndedAt = DateTimeOffset.UtcNow.AddDays(-daysAgo),
        PlannedDuration = TimeSpan.FromMinutes(minutes),
        ActualDuration = TimeSpan.FromMinutes(minutes),
        SessionNumber = 1
    };

    private static SessionHistoryService Service(ISessionHistoryStore store, TimeProvider? clock = null) =>
        new(new TimerService(new TimerEngine(new FakeTimeProvider())), store, clock ?? TimeProvider.System);

    [Fact]
    public void NoHistory_StreakIsZero()
    {
        var service = Service(new JsonLinesSessionHistoryStore(HistoryPath));

        Assert.Equal(0, service.CurrentStreak());
    }

    [Fact]
    public void ConsecutiveDaysWithAMeaningfulSession_CountTowardTheStreak()
    {
        var store = new JsonLinesSessionHistoryStore(HistoryPath);
        store.Append(Record(TimerMode.Study, 25, daysAgo: 2));
        store.Append(Record(TimerMode.Study, 25, daysAgo: 1));
        store.Append(Record(TimerMode.Study, 25, daysAgo: 0));

        Assert.Equal(3, Service(store).CurrentStreak());
    }

    [Fact]
    public void AGapDayBreaksTheStreak()
    {
        var store = new JsonLinesSessionHistoryStore(HistoryPath);
        store.Append(Record(TimerMode.Study, 25, daysAgo: 3)); // isolated, before the gap
        store.Append(Record(TimerMode.Study, 25, daysAgo: 1));
        store.Append(Record(TimerMode.Study, 25, daysAgo: 0));

        // Day 2 is missing, so counting back from today stops after yesterday.
        Assert.Equal(2, Service(store).CurrentStreak());
    }

    [Fact]
    public void NoSessionToday_ButYesterdayHadOne_StreakIsStillAlive()
    {
        var store = new JsonLinesSessionHistoryStore(HistoryPath);
        store.Append(Record(TimerMode.Study, 25, daysAgo: 2));
        store.Append(Record(TimerMode.Study, 25, daysAgo: 1));

        // Today isn't over yet, so yesterday's session keeps the streak alive.
        Assert.Equal(2, Service(store).CurrentStreak());
    }

    [Fact]
    public void NoSessionInTheLastTwoDays_StreakIsBroken()
    {
        var store = new JsonLinesSessionHistoryStore(HistoryPath);
        store.Append(Record(TimerMode.Study, 25, daysAgo: 2));

        Assert.Equal(0, Service(store).CurrentStreak());
    }

    [Fact]
    public void StoppedAndSkippedSessions_CountTowardTheStreakToo()
    {
        // Most real sessions never run to zero, so requiring Completed would leave the
        // streak dark on days the user genuinely focused but stopped or skipped early.
        var store = new JsonLinesSessionHistoryStore(HistoryPath);
        store.Append(Record(TimerMode.Study, 10, SessionOutcome.Stopped, daysAgo: 1));
        store.Append(Record(TimerMode.Study, 10, SessionOutcome.Skipped, daysAgo: 0));

        Assert.Equal(2, Service(store).CurrentStreak());
    }

    [Fact]
    public void ASessionShorterThanTheMinimumDoesNotCountTowardTheStreak()
    {
        // An instant Stop is a change of mind, not a day of showing up, even though it's
        // long enough to be worth logging at all (MinimumLoggedDuration is 1 second).
        var store = new JsonLinesSessionHistoryStore(HistoryPath);
        store.Append(Record(TimerMode.Study, 2, SessionOutcome.Stopped, daysAgo: 0));

        Assert.Equal(0, Service(store).CurrentStreak());
    }

    [Fact]
    public void BreakSessionsNeverCountTowardTheStreak()
    {
        var store = new JsonLinesSessionHistoryStore(HistoryPath);
        store.Append(Record(TimerMode.Break, 25, SessionOutcome.Completed, daysAgo: 0));

        Assert.Equal(0, Service(store).CurrentStreak());
    }

    [Fact]
    public void ASessionThatCrossesMidnight_CountsForTheDayItEnded()
    {
        // Matches SinceFiltersByEndTime's precedent in SessionHistoryTests: EndedAt, not
        // StartedAt, decides which day a session belongs to.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 10, 0, 5, 0, TimeSpan.Zero));
        clock.SetLocalTimeZone(TimeZoneInfo.Utc);
        var store = new JsonLinesSessionHistoryStore(HistoryPath);

        store.Append(new SessionRecord
        {
            Mode = TimerMode.Study,
            Outcome = SessionOutcome.Completed,
            StartedAt = new DateTimeOffset(2026, 8, 9, 23, 55, 0, TimeSpan.Zero),
            EndedAt = new DateTimeOffset(2026, 8, 10, 0, 5, 0, TimeSpan.Zero),
            PlannedDuration = TimeSpan.FromMinutes(10),
            ActualDuration = TimeSpan.FromMinutes(10),
            SessionNumber = 1
        });

        Assert.Equal(1, Service(store, clock).CurrentStreak());
    }

    [Fact]
    public void TimeZoneDeterminesWhichLocalDayASessionBelongsTo()
    {
        // 23:30 UTC on Aug 9 is already 01:30 local the next day in UTC+2 — bucketing
        // must follow the configured local zone, not the UTC calendar date.
        var zone = TimeZoneInfo.CreateCustomTimeZone("UTC+2", TimeSpan.FromHours(2), "UTC+2", "UTC+2");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 10, 1, 0, 0, TimeSpan.Zero));
        clock.SetLocalTimeZone(zone);
        var store = new JsonLinesSessionHistoryStore(HistoryPath);

        store.Append(new SessionRecord
        {
            Mode = TimerMode.Study,
            Outcome = SessionOutcome.Completed,
            StartedAt = new DateTimeOffset(2026, 8, 9, 23, 0, 0, TimeSpan.Zero),
            EndedAt = new DateTimeOffset(2026, 8, 9, 23, 30, 0, TimeSpan.Zero),
            PlannedDuration = TimeSpan.FromMinutes(30),
            ActualDuration = TimeSpan.FromMinutes(30),
            SessionNumber = 1
        });

        Assert.Equal(1, Service(store, clock).CurrentStreak());
    }

    [Fact]
    public void CurrentStreakOnAnUnreadableLogReturnsZeroRatherThanThrowing()
    {
        var service = Service(new FailingHistoryStore());

        Assert.Equal(0, service.CurrentStreak());
    }

    [Fact]
    public void DailyFocusMinutes_SumsStudyTimePerLocalDay()
    {
        var store = new JsonLinesSessionHistoryStore(HistoryPath);
        store.Append(Record(TimerMode.Study, 25, daysAgo: 1));
        store.Append(Record(TimerMode.Study, 10, daysAgo: 1));
        store.Append(Record(TimerMode.Study, 15, daysAgo: 0));
        store.Append(Record(TimerMode.Break, 5, daysAgo: 0)); // breaks don't count

        var daily = Service(store).DailyFocusMinutesSince(null);

        Assert.Equal(2, daily.Count);
        Assert.Equal(35, daily[0].Minutes); // yesterday, oldest first
        Assert.Equal(15, daily[1].Minutes); // today
    }

    [Fact]
    public void DailyFocusMinutes_RespectsSince_SameAsGetRecords()
    {
        var store = new JsonLinesSessionHistoryStore(HistoryPath);
        store.Append(Record(TimerMode.Study, 25, daysAgo: 3));
        store.Append(Record(TimerMode.Study, 25, daysAgo: 0));

        var daily = Service(store).DailyFocusMinutesSince(DateTimeOffset.UtcNow.AddDays(-1));

        Assert.Single(daily);
    }

    [Fact]
    public void DailyFocusMinutes_OnAnUnreadableLogReturnsEmptyRatherThanThrowing()
    {
        var service = Service(new FailingHistoryStore());

        Assert.Empty(service.DailyFocusMinutesSince());
    }

    private sealed class FailingHistoryStore : ISessionHistoryStore
    {
        public void Append(SessionRecord record) => throw new IOException("Disk is read-only.");
        public IReadOnlyList<SessionRecord> Read(DateTimeOffset? since = null) =>
            throw new IOException("Disk is read-only.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
