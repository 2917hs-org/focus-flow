namespace FocusFlow.Domain.Models;

/// <summary>
/// User-adjustable settings. Persisted as JSON, so treat every field as untrusted on
/// load and run it through <see cref="Normalized"/>.
/// </summary>
/// <remarks>
/// This doubles as the app-wide settings blob — theme and launch-on-startup live here
/// alongside the timer values even though the engine ignores them, which keeps
/// persistence to a single file and a single storage interface.
/// </remarks>
public class TimerConfig
{
    public static readonly TimeSpan MinDuration = TimeSpan.FromMinutes(1);

    /// <summary>FR-001 caps study sessions at two hours.</summary>
    public static readonly TimeSpan MaxStudyDuration = TimeSpan.FromMinutes(120);

    /// <summary>FR-002 caps breaks at one hour.</summary>
    public static readonly TimeSpan MaxBreakDuration = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Bumped whenever the persisted shape changes. Stored in the file so a future
    /// import can recognise and migrate settings written by an older version, rather
    /// than having to guess from which fields happen to be present.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    public const int MinSessionCount = 1;
    public const int MaxSessionCount = 10;
    public const int MinVolume = 0;
    public const int MaxVolume = 100;

    public static readonly TimeSpan MinReminderLead = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxReminderLead = TimeSpan.FromMinutes(10);

    /// <summary>Schema of the file this was loaded from; see <see cref="CurrentSchemaVersion"/>.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public TimeSpan StudyDuration { get; set; } = TimeSpan.FromMinutes(25);

    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Roll straight into the break when a study session ends.</summary>
    public bool AutoStartBreak { get; set; } = true;

    /// <summary>Roll straight into the next study session when a break ends.</summary>
    public bool AutoStartStudy { get; set; } = true;

    /// <summary>
    /// FR-006. When true the study/break cycle repeats forever and
    /// <see cref="SessionCount"/> is ignored.
    /// </summary>
    public bool InfiniteMode { get; set; } = true;

    /// <summary>FR-007. Study sessions to run before the timer stops, when not infinite.</summary>
    public int SessionCount { get; set; } = 4;

    /// <summary>
    /// Platform token for the alarm: a file path (WAV/MP3), or a Windows system-sound
    /// alias. Null means "use the platform default".
    /// </summary>
    public string? AlarmSoundPath { get; set; }

    /// <summary>FR-009. Alarm playback volume, 0-100.</summary>
    public int AlarmVolume { get; set; } = 80;

    /// <summary>FR-010. Audio file to play once a break finishes.</summary>
    public string? MusicPath { get; set; }

    public bool PlayMusicAfterBreak { get; set; }

    /// <summary>FR-012. Register the app to start when the user logs in.</summary>
    public bool LaunchOnStartup { get; set; }

    /// <summary>
    /// Warn shortly before a session ends. This replaces the always-visible tray
    /// countdown: rather than watching the clock, the user gets one nudge as the session
    /// closes out.
    /// </summary>
    public bool ReminderEnabled { get; set; } = true;

    /// <summary>How long before the end the reminder fires.</summary>
    public TimeSpan ReminderLeadTime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>FR-015.</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;

    public TimerConfig Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        StudyDuration = StudyDuration,
        BreakDuration = BreakDuration,
        AutoStartBreak = AutoStartBreak,
        AutoStartStudy = AutoStartStudy,
        InfiniteMode = InfiniteMode,
        SessionCount = SessionCount,
        AlarmSoundPath = AlarmSoundPath,
        AlarmVolume = AlarmVolume,
        MusicPath = MusicPath,
        PlayMusicAfterBreak = PlayMusicAfterBreak,
        LaunchOnStartup = LaunchOnStartup,
        ReminderEnabled = ReminderEnabled,
        ReminderLeadTime = ReminderLeadTime,
        Theme = Theme
    };

    /// <summary>
    /// Clamps every field into a usable range. A hand-edited or corrupt config file could
    /// otherwise hand the engine a zero duration, which would end sessions instantly in a
    /// loop.
    /// </summary>
    public TimerConfig Normalized()
    {
        var clone = Clone();

        clone.StudyDuration = Clamp(clone.StudyDuration, TimeSpan.FromMinutes(25), MaxStudyDuration);
        clone.BreakDuration = Clamp(clone.BreakDuration, TimeSpan.FromMinutes(5), MaxBreakDuration);

        clone.SessionCount = Math.Clamp(clone.SessionCount, MinSessionCount, MaxSessionCount);
        clone.AlarmVolume = Math.Clamp(clone.AlarmVolume, MinVolume, MaxVolume);

        clone.ReminderLeadTime = clone.ReminderLeadTime < MinReminderLead ? MinReminderLead
            : clone.ReminderLeadTime > MaxReminderLead ? MaxReminderLead
            : clone.ReminderLeadTime;

        if (string.IsNullOrWhiteSpace(clone.AlarmSoundPath))
        {
            clone.AlarmSoundPath = null;
        }

        if (string.IsNullOrWhiteSpace(clone.MusicPath))
        {
            clone.MusicPath = null;
            clone.PlayMusicAfterBreak = false;
        }

        if (!Enum.IsDefined(clone.Theme))
        {
            clone.Theme = AppTheme.System;
        }

        // A file written before versioning existed deserialises to 0; treat it as v1
        // rather than as something from the future.
        if (clone.SchemaVersion <= 0)
        {
            clone.SchemaVersion = CurrentSchemaVersion;
        }

        return clone;
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan fallback, TimeSpan max)
    {
        if (value <= TimeSpan.Zero)
        {
            return fallback;
        }

        return value < MinDuration ? MinDuration
            : value > max ? max
            : value;
    }
}
