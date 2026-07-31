namespace FocusFlow.Domain.Models;

/// <summary>How a session came to an end.</summary>
public enum SessionOutcome
{
    /// <summary>Ran to zero.</summary>
    Completed,

    /// <summary>The user pressed Skip.</summary>
    Skipped,

    /// <summary>The user stopped the run part-way through.</summary>
    Stopped
}

/// <summary>
/// One finished study or break session, as written to the history log.
/// </summary>
/// <remarks>
/// <para>
/// Shaped for later reporting rather than for display: timestamps are UTC and absolute,
/// and both the planned and the actual duration are kept, so totals, completion rates and
/// per-day/per-week groupings can all be derived without re-deriving anything from the
/// app's current settings.
/// </para>
/// <para>
/// <see cref="SchemaVersion"/> is stored on every record because the log is append-only —
/// old records stay on disk unchanged, so a reader has to be able to tell which shape each
/// line is.
/// </para>
/// </remarks>
public sealed record SessionRecord
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public TimerMode Mode { get; init; }

    public SessionOutcome Outcome { get; init; }

    /// <summary>UTC, so a timezone change or DST shift can't reorder the log.</summary>
    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset EndedAt { get; init; }

    /// <summary>What the session was configured to run for.</summary>
    public TimeSpan PlannedDuration { get; init; }

    /// <summary>What was actually spent — less than planned when skipped or stopped.</summary>
    public TimeSpan ActualDuration { get; init; }

    /// <summary>Which session of the run this was.</summary>
    public int SessionNumber { get; init; }
}
