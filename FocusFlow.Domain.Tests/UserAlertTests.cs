using FocusFlow.Application.Interfaces;
using FocusFlow.Application.Services;
using FocusFlow.Domain.Engines;
using FocusFlow.Domain.Models;
using Microsoft.Extensions.Time.Testing;

namespace FocusFlow.Domain.Tests;

/// <summary>
/// Storage failures must reach the user instead of being swallowed, and must not repeat.
/// </summary>
public class UserAlertTests
{
    private sealed class RecordingAlerts : IUserAlerts
    {
        public List<(string Key, string Heading, string Body)> Alerts { get; } = [];

        public void Report(string key, string heading, string body) =>
            Alerts.Add((key, heading, body));
    }

    private sealed class RecordingLogger : IAppLogger
    {
        public List<string> Errors { get; } = [];

        public void Info(string message) { }
        public void Warn(string message) { }

        public void Error(string message, Exception? exception = null) =>
            Errors.Add(message);
    }

    private sealed class FailingConfigStorage : IConfigStorage
    {
        public bool FailLoad { get; init; }

        public TimerConfig Load() =>
            FailLoad ? throw new UnauthorizedAccessException("Access to config.json is denied.")
                     : new TimerConfig();

        public void Save(TimerConfig config) =>
            throw new UnauthorizedAccessException("Access to config.json is denied.");
    }

    private sealed class FailingSessionStore : ISessionStateStorage
    {
        public SessionState? Load() => null;
        public void Save(SessionState state) => throw new IOException("Disk is read-only.");
        public void Clear() { }
    }

    private sealed class FailingHistoryStore : ISessionHistoryStore
    {
        public void Append(SessionRecord record) => throw new IOException("Disk is read-only.");
        public IReadOnlyList<SessionRecord> Read(DateTimeOffset? since = null) => [];
    }

    [Fact]
    public void AFailedSettingsSaveIsReportedToTheUser()
    {
        var alerts = new RecordingAlerts();
        var clock = new FakeTimeProvider();
        using var settings = new SettingsService(new FailingConfigStorage(), clock, alerts);

        settings.Update(c => c.StudyDuration = TimeSpan.FromMinutes(30));
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Single(alerts.Alerts);
        Assert.Equal("settings-save", alerts.Alerts[0].Key);
        Assert.Contains("can't save your settings", alerts.Alerts[0].Heading);
    }

    [Fact]
    public void TheSameFailureSurfacesOnlyOnce()
    {
        // Dedup belongs to the sink, not the reporting service: the snapshot retries on a
        // timer, so an unwritable folder would otherwise raise a dialog every few seconds.
        var raised = new List<string>();
        var sink = new UserAlerts();
        sink.AlertRaised += (_, e) => raised.Add(e.Heading);

        var clock = new FakeTimeProvider();
        using var settings = new SettingsService(new FailingConfigStorage(), clock, sink);

        for (var i = 1; i <= 5; i++)
        {
            settings.Update(c => c.StudyDuration = TimeSpan.FromMinutes(20 + i));
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Single(raised);
    }

    [Fact]
    public void DifferentFailuresEachSurfaceOnce()
    {
        var raised = new List<string>();
        var sink = new UserAlerts();
        sink.AlertRaised += (_, e) => raised.Add(e.Heading);

        sink.Report("settings-save", "A", "body");
        sink.Report("settings-save", "A", "body");
        sink.Report("history-save", "B", "body");

        Assert.Equal(2, raised.Count);
    }

    [Fact]
    public void AnUnreadableSettingsFileIsReported()
    {
        var alerts = new RecordingAlerts();
        using var settings = new SettingsService(
            new FailingConfigStorage { FailLoad = true }, new FakeTimeProvider(), alerts);

        Assert.Contains(alerts.Alerts, a => a.Key == "settings-load");
    }

    [Fact]
    public void TheFailureDetailIsIncludedSoTheUserCanActOnIt()
    {
        var alerts = new RecordingAlerts();
        var clock = new FakeTimeProvider();
        using var settings = new SettingsService(new FailingConfigStorage(), clock, alerts);

        settings.Update(c => c.BreakDuration = TimeSpan.FromMinutes(7));
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Contains("config.json", alerts.Alerts[0].Body);
    }

    [Fact]
    public async Task AFailedSessionSnapshotIsReported()
    {
        var alerts = new RecordingAlerts();
        var clock = new FakeTimeProvider();
        var service = new TimerService(new TimerEngine(clock));
        using var persistence = new SessionPersistenceService(
            service, new FailingSessionStore(), clock, alerts);
        persistence.StartTracking(new TimerConfig());

        await service.StartAsync(new TimerConfig());
        clock.Run(TimeSpan.FromSeconds(10));

        Assert.Contains(alerts.Alerts, a => a.Key == "session-save");
    }

    [Fact]
    public async Task AFailedHistoryWriteIsReported()
    {
        var alerts = new RecordingAlerts();
        var clock = new FakeTimeProvider();
        var service = new TimerService(new TimerEngine(clock));
        using var history = new SessionHistoryService(
            service, new FailingHistoryStore(), clock, alerts);
        history.StartTracking();

        await service.StartAsync(new TimerConfig { StudyDuration = TimeSpan.FromMinutes(1) });
        clock.Run(TimeSpan.FromMinutes(1));

        Assert.Contains(alerts.Alerts, a => a.Key == "history-save");
    }

    [Fact]
    public void AReportedFailureIsAlsoLogged()
    {
        // The dialog is transient; the log is what's left once it's dismissed.
        var logger = new RecordingLogger();
        var sink = new UserAlerts(logger);

        sink.Report("settings-save", "FocusFlow can't save your settings", "disk is read-only");

        var entry = Assert.Single(logger.Errors);
        Assert.Contains("FocusFlow can't save your settings", entry);
        Assert.Contains("disk is read-only", entry);
    }

    [Fact]
    public void ARepeatedFailureIsLoggedOnlyOnce()
    {
        var logger = new RecordingLogger();
        var sink = new UserAlerts(logger);

        for (var i = 0; i < 5; i++)
        {
            sink.Report("settings-save", "heading", "body");
        }

        Assert.Single(logger.Errors);
    }

    [Fact]
    public async Task AFailedWriteStillDoesNotStopTheTimer()
    {
        // The original behaviour that must survive: reporting is additive, the session
        // keeps running regardless of what the disk is doing.
        var alerts = new RecordingAlerts();
        var clock = new FakeTimeProvider();
        var service = new TimerService(new TimerEngine(clock));
        using var persistence = new SessionPersistenceService(
            service, new FailingSessionStore(), clock, alerts);
        persistence.StartTracking(new TimerConfig());

        await service.StartAsync(new TimerConfig { StudyDuration = TimeSpan.FromMinutes(25) });
        clock.Run(TimeSpan.FromSeconds(30));

        Assert.Equal(TimerMode.Study, service.CurrentState.Mode);
        Assert.Equal(TimeSpan.FromMinutes(25) - TimeSpan.FromSeconds(30), service.CurrentState.RemainingTime);
    }
}
