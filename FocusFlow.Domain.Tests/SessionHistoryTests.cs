using FocusFlow.Application.Interfaces;
using FocusFlow.Application.Services;
using FocusFlow.Domain.Engines;
using FocusFlow.Domain.Models;
using FocusFlow.Infrastructure.Storage;
using Microsoft.Extensions.Time.Testing;

namespace FocusFlow.Domain.Tests;

/// <summary>
/// Session history: the append-only log and the aggregate reporting will be built on.
/// </summary>
public class SessionHistoryTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "focusflow-history-tests", Guid.NewGuid().ToString("N"));

    private string HistoryPath => Path.Combine(_directory, "nested", "history.jsonl");

    private static TimerConfig Config(int study = 1, int @break = 1) => new()
    {
        StudyDuration = TimeSpan.FromMinutes(study),
        BreakDuration = TimeSpan.FromMinutes(@break)
    };

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

    [Fact]
    public void AppendCreatesTheFileAndRoundTrips()
    {
        var store = new JsonLinesSessionHistoryStore(HistoryPath);
        var record = Record(TimerMode.Study, 25);

        store.Append(record);
        var read = store.Read();

        Assert.Single(read);
        Assert.Equal(TimerMode.Study, read[0].Mode);
        Assert.Equal(SessionOutcome.Completed, read[0].Outcome);
        Assert.Equal(TimeSpan.FromMinutes(25), read[0].ActualDuration);
        Assert.Equal(SessionRecord.CurrentSchemaVersion, read[0].SchemaVersion);
    }

    [Fact]
    public void RecordsAccumulateInOrder()
    {
        var store = new JsonLinesSessionHistoryStore(HistoryPath);

        store.Append(Record(TimerMode.Study, 25));
        store.Append(Record(TimerMode.Break, 5));
        store.Append(Record(TimerMode.Study, 25));

        var read = store.Read();
        Assert.Equal(3, read.Count);
        Assert.Equal(TimerMode.Break, read[1].Mode);
    }

    [Fact]
    public void OneLinePerRecord_SoTheLogStaysStreamable()
    {
        var store = new JsonLinesSessionHistoryStore(HistoryPath);
        store.Append(Record(TimerMode.Study, 25));
        store.Append(Record(TimerMode.Break, 5));

        var lines = File.ReadAllLines(HistoryPath).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void ATornFinalLineDoesNotDestroyEarlierHistory()
    {
        // What a process killed mid-append leaves behind. The whole point of choosing
        // JSONL over a single JSON array is that this costs one record, not the file.
        var store = new JsonLinesSessionHistoryStore(HistoryPath);
        store.Append(Record(TimerMode.Study, 25));
        store.Append(Record(TimerMode.Study, 25));
        File.AppendAllText(HistoryPath, "{\"Mode\":\"Stu");

        var read = store.Read();

        Assert.Equal(2, read.Count);
    }

    [Fact]
    public void ReadingAnAbsentLogReturnsEmptyRatherThanThrowing()
    {
        Assert.Empty(new JsonLinesSessionHistoryStore(HistoryPath).Read());
    }

    [Fact]
    public void ARecordWrittenBeforeLabelExistedDeserialisesWithANullLabel()
    {
        // What every line already on disk before this feature shipped looks like — no
        // "Label" property at all, not even a JSON null.
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
        File.WriteAllText(HistoryPath,
            "{\"SchemaVersion\":1,\"Id\":\"abc\",\"Mode\":\"Study\",\"Outcome\":\"Completed\","
            + "\"StartedAt\":\"2026-07-30T09:00:00+00:00\",\"EndedAt\":\"2026-07-30T09:25:00+00:00\","
            + "\"PlannedDuration\":\"00:25:00\",\"ActualDuration\":\"00:25:00\",\"SessionNumber\":1}"
            + Environment.NewLine);

        var read = new JsonLinesSessionHistoryStore(HistoryPath).Read();

        Assert.Single(read);
        Assert.Null(read[0].Label);
    }

    [Fact]
    public void ASessionStartedWithALabelRoundTripsItThroughAppendAndRead()
    {
        var store = new JsonLinesSessionHistoryStore(HistoryPath);
        store.Append(Record(TimerMode.Study, 25) with { Label = "Thesis chapter 3" });

        var read = store.Read();

        Assert.Single(read);
        Assert.Equal("Thesis chapter 3", read[0].Label);
    }

    [Fact]
    public void SinceFiltersByEndTime_WhichIsWhatPerDayReportingNeeds()
    {
        var store = new JsonLinesSessionHistoryStore(HistoryPath);
        store.Append(Record(TimerMode.Study, 25, daysAgo: 3));
        store.Append(Record(TimerMode.Study, 25));

        var recent = store.Read(DateTimeOffset.UtcNow.AddDays(-1));

        Assert.Single(recent);
    }

    [Fact]
    public void SummariseAggregatesStudyAndBreakSeparately()
    {
        var store = new JsonLinesSessionHistoryStore(HistoryPath);
        store.Append(Record(TimerMode.Study, 25));
        store.Append(Record(TimerMode.Break, 5));
        store.Append(Record(TimerMode.Study, 25));
        store.Append(Record(TimerMode.Study, 10, SessionOutcome.Stopped));

        var service = new SessionHistoryService(
            new TimerService(new TimerEngine(new FakeTimeProvider())),
            store,
            TimeProvider.System);

        var summary = service.Summarise();

        // Only the two that ran to completion count as completed sessions...
        Assert.Equal(2, summary.CompletedStudySessions);
        // ...but the abandoned 10 minutes were still time spent.
        Assert.Equal(TimeSpan.FromMinutes(60), summary.TotalStudyTime);
        Assert.Equal(TimeSpan.FromMinutes(5), summary.TotalBreakTime);
    }

    [Fact]
    public void LabelTotalsGroupStudyTimeByLabel_BusiestFirst_UnlabelledIncluded()
    {
        var store = new JsonLinesSessionHistoryStore(HistoryPath);
        store.Append(Record(TimerMode.Study, 25) with { Label = "Thesis chapter 3" });
        store.Append(Record(TimerMode.Study, 10) with { Label = "Thesis chapter 3" });
        store.Append(Record(TimerMode.Study, 15) with { Label = "Inbox zero" });
        store.Append(Record(TimerMode.Study, 5));
        // Break time under the same label as a labelled study run is still excluded — this
        // is study-only, matching Summarise's "focused time" framing.
        store.Append(Record(TimerMode.Break, 5) with { Label = "Thesis chapter 3" });

        var service = new SessionHistoryService(
            new TimerService(new TimerEngine(new FakeTimeProvider())), store, TimeProvider.System);

        var totals = service.LabelTotalsSince();

        Assert.Equal(3, totals.Count);
        Assert.Equal("Thesis chapter 3", totals[0].Label);
        Assert.Equal(TimeSpan.FromMinutes(35), totals[0].TotalTime);
        Assert.Equal(2, totals[0].SessionCount);
        Assert.Contains(totals, t => t.Label == "Inbox zero" && t.TotalTime == TimeSpan.FromMinutes(15));
        Assert.Contains(totals, t => t.Label is null && t.TotalTime == TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void LabelTotalsGroupLabelsCaseInsensitively_MatchingHowTheHistoryFilterMatches()
    {
        // "Thesis" and "thesis" must land in the same bucket here, or the History window's
        // by-label breakdown and its label filter would disagree on how many sessions a
        // label covers — the filter already matches case-insensitively.
        var store = new JsonLinesSessionHistoryStore(HistoryPath);
        store.Append(Record(TimerMode.Study, 25) with { Label = "Thesis chapter 3" });
        store.Append(Record(TimerMode.Study, 10) with { Label = "thesis chapter 3" });

        var service = new SessionHistoryService(
            new TimerService(new TimerEngine(new FakeTimeProvider())), store, TimeProvider.System);

        var totals = service.LabelTotalsSince();

        Assert.Single(totals);
        Assert.Equal(TimeSpan.FromMinutes(35), totals[0].TotalTime);
        Assert.Equal(2, totals[0].SessionCount);
    }

    [Fact]
    public void LabelTotalsOnAnUnreadableLogReturnsEmptyRatherThanThrowing()
    {
        var service = new SessionHistoryService(
            new TimerService(new TimerEngine(new FakeTimeProvider())),
            new FailingHistoryStore(),
            TimeProvider.System);

        Assert.Empty(service.LabelTotalsSince());
    }

    [Fact]
    public void GetRecordsReturnsNewestFirst_ForAReportThatReadsTopToBottom()
    {
        var store = new JsonLinesSessionHistoryStore(HistoryPath);
        store.Append(Record(TimerMode.Study, 25, daysAgo: 2));
        store.Append(Record(TimerMode.Break, 5, daysAgo: 1));
        store.Append(Record(TimerMode.Study, 25, daysAgo: 0));

        var service = new SessionHistoryService(
            new TimerService(new TimerEngine(new FakeTimeProvider())), store, TimeProvider.System);

        var records = service.GetRecords();

        Assert.Equal(3, records.Count);
        Assert.True(records[0].EndedAt >= records[1].EndedAt);
        Assert.True(records[1].EndedAt >= records[2].EndedAt);
    }

    [Fact]
    public void GetRecordsRespectsSince_SameAsSummarise()
    {
        var store = new JsonLinesSessionHistoryStore(HistoryPath);
        store.Append(Record(TimerMode.Study, 25, daysAgo: 3));
        store.Append(Record(TimerMode.Study, 25));

        var service = new SessionHistoryService(
            new TimerService(new TimerEngine(new FakeTimeProvider())), store, TimeProvider.System);

        var recent = service.GetRecords(DateTimeOffset.UtcNow.AddDays(-1));

        Assert.Single(recent);
    }

    [Fact]
    public void GetRecordsOnAnUnreadableLogReturnsEmptyRatherThanThrowing()
    {
        var service = new SessionHistoryService(
            new TimerService(new TimerEngine(new FakeTimeProvider())),
            new FailingHistoryStore(),
            TimeProvider.System);

        Assert.Empty(service.GetRecords());
    }

    private sealed class FailingHistoryStore : ISessionHistoryStore
    {
        public void Append(SessionRecord record) => throw new IOException("Disk is read-only.");
        public IReadOnlyList<SessionRecord> Read(DateTimeOffset? since = null) =>
            throw new IOException("Disk is read-only.");
    }

    [Fact]
    public async Task AFinishedSessionIsLoggedAutomatically()
    {
        var clock = new FakeTimeProvider();
        var store = new JsonLinesSessionHistoryStore(HistoryPath);
        var service = new TimerService(new TimerEngine(clock));
        using var history = new SessionHistoryService(service, store, clock);
        history.StartTracking();

        await service.StartAsync(Config(study: 1, @break: 1));
        clock.Run(TimeSpan.FromMinutes(1));

        var read = store.Read();
        Assert.Single(read);
        Assert.Equal(TimerMode.Study, read[0].Mode);
        Assert.Equal(SessionOutcome.Completed, read[0].Outcome);
        Assert.Equal(TimeSpan.FromMinutes(1), read[0].ActualDuration);
    }

    [Fact]
    public async Task ALabelGivenAtStartIsCarriedThroughToTheLoggedRecord()
    {
        var clock = new FakeTimeProvider();
        var store = new JsonLinesSessionHistoryStore(HistoryPath);
        var service = new TimerService(new TimerEngine(clock));
        using var history = new SessionHistoryService(service, store, clock);
        history.StartTracking();

        await service.StartAsync(Config(study: 1, @break: 1), "Thesis chapter 3");
        clock.Run(TimeSpan.FromMinutes(1));

        var read = store.Read();
        Assert.Single(read);
        Assert.Equal("Thesis chapter 3", read[0].Label);
    }

    [Fact]
    public async Task StartingWithNoLabelLogsANullLabel()
    {
        var clock = new FakeTimeProvider();
        var store = new JsonLinesSessionHistoryStore(HistoryPath);
        var service = new TimerService(new TimerEngine(clock));
        using var history = new SessionHistoryService(service, store, clock);
        history.StartTracking();

        await service.StartAsync(Config(study: 1, @break: 1));
        clock.Run(TimeSpan.FromMinutes(1));

        var read = store.Read();
        Assert.Single(read);
        Assert.Null(read[0].Label);
    }

    [Fact]
    public async Task StoppingPartWayThroughStillRecordsTheTimeSpent()
    {
        var clock = new FakeTimeProvider();
        var store = new JsonLinesSessionHistoryStore(HistoryPath);
        var service = new TimerService(new TimerEngine(clock));
        using var history = new SessionHistoryService(service, store, clock);
        history.StartTracking();

        await service.StartAsync(Config(study: 25));
        clock.Run(TimeSpan.FromMinutes(4));
        service.Stop();

        var read = store.Read();
        Assert.Single(read);
        Assert.Equal(SessionOutcome.Stopped, read[0].Outcome);
        Assert.Equal(TimeSpan.FromMinutes(4), read[0].ActualDuration);
        Assert.Equal(TimeSpan.FromMinutes(25), read[0].PlannedDuration);
    }

    [Fact]
    public async Task SkippingIsRecordedAsSkippedWithTheTimeActuallySpent()
    {
        var clock = new FakeTimeProvider();
        var store = new JsonLinesSessionHistoryStore(HistoryPath);
        var service = new TimerService(new TimerEngine(clock));
        using var history = new SessionHistoryService(service, store, clock);
        history.StartTracking();

        await service.StartAsync(Config(study: 25));
        clock.Run(TimeSpan.FromMinutes(2));
        service.Skip();

        var read = store.Read();
        Assert.Single(read);
        Assert.Equal(SessionOutcome.Skipped, read[0].Outcome);
        Assert.Equal(TimeSpan.FromMinutes(2), read[0].ActualDuration);
    }

    [Fact]
    public async Task AnImmediateStartStopIsNotLogged()
    {
        var clock = new FakeTimeProvider();
        var store = new JsonLinesSessionHistoryStore(HistoryPath);
        var service = new TimerService(new TimerEngine(clock));
        using var history = new SessionHistoryService(service, store, clock);
        history.StartTracking();

        await service.StartAsync(Config(study: 25));
        service.Stop();

        // A misclick is not productivity history.
        Assert.Empty(store.Read());
    }

    [Fact]
    public void SettingsCarryASchemaVersionForFutureImports()
    {
        var path = Path.Combine(_directory, "config.json");
        var storage = new JsonConfigStorage(path);

        storage.Save(new TimerConfig { StudyDuration = TimeSpan.FromMinutes(40) });

        Assert.Contains("SchemaVersion", File.ReadAllText(path));
        Assert.Equal(TimerConfig.CurrentSchemaVersion, storage.Load().Normalized().SchemaVersion);
    }

    [Fact]
    public void SettingsWrittenBeforeVersioningLoadAsVersionOne()
    {
        var path = Path.Combine(_directory, "legacy.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, "{\"StudyDuration\":\"00:30:00\"}");

        var loaded = new JsonConfigStorage(path).Load().Normalized();

        Assert.Equal(TimerConfig.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(TimeSpan.FromMinutes(30), loaded.StudyDuration);
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
