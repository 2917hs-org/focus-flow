using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FocusFlow.App.Services;
using FocusFlow.Application.Interfaces;
using FocusFlow.Application.Services;
using FocusFlow.Domain.Interfaces;
using FocusFlow.Domain.Models;

namespace FocusFlow.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly ITimerService _timerService;
    private readonly INotificationService _notificationService;
    private readonly IAudioPlayer _audioPlayer;
    private readonly ITrayService _trayService;
    private readonly ISettingsService _settings;
    private readonly IStartupService _startupService;
    private readonly IFilePickerService _filePicker;
    private readonly SessionHistoryService _history;

    /// <summary>
    /// Raised for things the user must actually see. App owns the window; the ViewModel
    /// stays free of Window references.
    /// </summary>
    public event EventHandler<(string Heading, string Body)>? AlertRequested;

    /// <summary>"View History" chosen — show the session history window. Same reasoning
    /// as <see cref="AlertRequested"/>: App owns the window, not the ViewModel.</summary>
    public event EventHandler? ShowHistoryRequested;

    /// <summary>
    /// Set while pushing stored settings into the bound properties, so the resulting
    /// change notifications don't loop straight back into <see cref="ISettingsService"/>.
    /// </summary>
    private bool _loadingSettings;

    private bool _disposed;

    [ObservableProperty] private int _studyDuration;
    [ObservableProperty] private int _breakDuration;
    [ObservableProperty] private bool _autoStartBreak;
    [ObservableProperty] private bool _autoStartStudy;
    [ObservableProperty] private bool _infiniteMode;
    [ObservableProperty] private int _sessionCount;
    [ObservableProperty] private AlarmSound? _selectedSound;
    [ObservableProperty] private int _alarmVolume;
    [ObservableProperty] private string? _musicPath;
    [ObservableProperty] private bool _playMusicAfterBreak;
    [ObservableProperty] private bool _launchOnStartup;
    [ObservableProperty] private AppTheme _theme;
    [ObservableProperty] private bool _reminderEnabled;
    [ObservableProperty] private int _reminderLeadMinutes;

    [ObservableProperty] private string _currentTime = "25:00";
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string? _startupWarning;

    /// <summary>
    /// Colours the countdown and progress bar by what is running, so the mode is readable
    /// at a glance without parsing the status line. Indigo matches the app icon.
    /// </summary>
    [ObservableProperty] private IBrush _modeBrush = ModeBrushes.Idle;
    [ObservableProperty] private string _todaySummary = "No sessions yet today";

    public MainWindowViewModel(
        ITimerService timerService,
        INotificationService notificationService,
        IAudioPlayer audioPlayer,
        ITrayService trayService,
        ISettingsService settings,
        IStartupService startupService,
        IFilePickerService filePicker,
        SessionHistoryService history)
    {
        _timerService = timerService;
        _notificationService = notificationService;
        _audioPlayer = audioPlayer;
        _trayService = trayService;
        _settings = settings;
        _startupService = startupService;
        _filePicker = filePicker;
        _history = history;

        AvailableSounds = new ObservableCollection<AlarmSound>(audioPlayer.AvailableSounds);

        LoadFromSettings();

        _timerService.TimerUpdated += OnTimerUpdated;
        _timerService.SessionEnded += OnSessionEnded;
        _timerService.SystemResumed += OnSystemResumed;
        _timerService.ReminderDue += OnReminderDue;

        Apply(_timerService.CurrentState);
        RefreshTodaySummary();
    }

    public ObservableCollection<AlarmSound> AvailableSounds { get; }

    public IReadOnlyList<AppTheme> AvailableThemes { get; } =
        [AppTheme.System, AppTheme.Light, AppTheme.Dark];

    /// <summary>Quick-pick options in the Focus dropdown; any value up to 120 can still be typed.</summary>
    public IReadOnlyList<int> FocusDurationPresets { get; } = [15, 30, 45, 60, 75, 90, 105, 120];

    /// <summary>Quick-pick options in the Break dropdown; any value up to 60 can still be typed.</summary>
    public IReadOnlyList<int> BreakDurationPresets { get; } = [5, 10, 15, 20, 30, 45, 60];

    public bool IsStartupSupported => _startupService.IsSupported;

    /// <summary>Session count only applies when the run is finite (FR-006 vs FR-007).</summary>
    public bool IsSessionCountEnabled => !InfiniteMode;

    private void LoadFromSettings()
    {
        var config = _settings.Current;

        _loadingSettings = true;
        try
        {
            StudyDuration = (int)Math.Round(config.StudyDuration.TotalMinutes);
            BreakDuration = (int)Math.Round(config.BreakDuration.TotalMinutes);
            AutoStartBreak = config.AutoStartBreak;
            AutoStartStudy = config.AutoStartStudy;
            InfiniteMode = config.InfiniteMode;
            SessionCount = config.SessionCount;
            AlarmVolume = config.AlarmVolume;
            MusicPath = config.MusicPath;
            PlayMusicAfterBreak = config.PlayMusicAfterBreak;
            Theme = config.Theme;
            ReminderEnabled = config.ReminderEnabled;
            ReminderLeadMinutes = Math.Max(1, (int)Math.Round(config.ReminderLeadTime.TotalMinutes));
            SelectedSound = ResolveSound(config.AlarmSoundPath);

            // Trust the OS over the config file: the user may have removed the login item
            // outside the app, and the checkbox should reflect reality.
            LaunchOnStartup = _startupService.IsSupported
                ? _startupService.IsEnabled()
                : config.LaunchOnStartup;
        }
        finally
        {
            _loadingSettings = false;
        }

        ApplyTheme(Theme);
    }

    /// <summary>
    /// Finds the stored sound in the built-in list, adding an entry for a custom file so
    /// the picker can show what is actually selected.
    /// </summary>
    private AlarmSound? ResolveSound(string? value)
    {
        var match = AvailableSounds.FirstOrDefault(s => s.Value == value);
        if (match is not null)
        {
            return match;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return AvailableSounds.FirstOrDefault();
        }

        var custom = new AlarmSound(Path.GetFileName(value), value);
        AvailableSounds.Add(custom);
        return custom;
    }

    // Generated by [ObservableProperty]; each persists the edit. Study/Break are now
    // editable combo boxes rather than a NumericUpDown, so nothing upstream of this
    // clamps a typed value — FR-001/FR-002 (120 min / 60 min) are enforced here instead.
    partial void OnStudyDurationChanged(int value)
    {
        var clamped = Math.Clamp(value, (int)TimerConfig.MinDuration.TotalMinutes,
            (int)TimerConfig.MaxStudyDuration.TotalMinutes);
        if (clamped != value)
        {
            StudyDuration = clamped;
            return;
        }

        Persist(c => c.StudyDuration = TimeSpan.FromMinutes(value));
    }

    partial void OnBreakDurationChanged(int value)
    {
        var clamped = Math.Clamp(value, (int)TimerConfig.MinDuration.TotalMinutes,
            (int)TimerConfig.MaxBreakDuration.TotalMinutes);
        if (clamped != value)
        {
            BreakDuration = clamped;
            return;
        }

        Persist(c => c.BreakDuration = TimeSpan.FromMinutes(value));
    }

    partial void OnAutoStartBreakChanged(bool value) => Persist(c => c.AutoStartBreak = value);

    partial void OnAutoStartStudyChanged(bool value) => Persist(c => c.AutoStartStudy = value);

    partial void OnSessionCountChanged(int value) => Persist(c => c.SessionCount = value);

    partial void OnAlarmVolumeChanged(int value) => Persist(c => c.AlarmVolume = value);

    partial void OnReminderEnabledChanged(bool value) => Persist(c => c.ReminderEnabled = value);

    partial void OnReminderLeadMinutesChanged(int value) =>
        Persist(c => c.ReminderLeadTime = TimeSpan.FromMinutes(value));

    partial void OnMusicPathChanged(string? value) => Persist(c => c.MusicPath = value);

    partial void OnPlayMusicAfterBreakChanged(bool value) => Persist(c => c.PlayMusicAfterBreak = value);

    partial void OnSelectedSoundChanged(AlarmSound? value) =>
        Persist(c => c.AlarmSoundPath = value?.Value);

    partial void OnInfiniteModeChanged(bool value)
    {
        Persist(c => c.InfiniteMode = value);
        OnPropertyChanged(nameof(IsSessionCountEnabled));
    }

    partial void OnThemeChanged(AppTheme value)
    {
        Persist(c => c.Theme = value);
        ApplyTheme(value);
    }

    partial void OnLaunchOnStartupChanged(bool value)
    {
        if (_loadingSettings)
        {
            return;
        }

        // Only record the preference if the OS actually accepted the change, so the
        // checkbox can't claim something that didn't happen.
        if (_startupService.SetEnabled(value))
        {
            StartupWarning = null;
            Persist(c => c.LaunchOnStartup = value);
        }
        else
        {
            StartupWarning = _startupService.IsSupported
                ? "Couldn't update the login item."
                : OperatingSystem.IsMacOS()
                    ? "Launch at login needs the packaged FocusFlow.app, not a dev build."
                    : "Manage this in Settings > Apps > Startup, or use a non-packaged build.";
        }
    }

    private static void ApplyTheme(AppTheme theme)
    {
        if (Avalonia.Application.Current is not { } app)
        {
            return;
        }

        app.RequestedThemeVariant = theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }

    private void Persist(Action<TimerConfig> mutate)
    {
        if (_loadingSettings)
        {
            return;
        }

        _settings.Update(mutate);
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        // Settings are already normalized and persisted, so the engine gets a validated
        // config rather than whatever is currently typed in the boxes.
        await _timerService.StartAsync(_settings.Current);
    }

    /// <summary>FR-002. Runs a break on its own.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartBreak() => await _timerService.StartBreakAsync(_settings.Current);

    /// <summary>
    /// Starts a fixed-length focus session that stops when it's done. Nothing follows it,
    /// so going again is a deliberate choice rather than something that just happens.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartPredefined(int minutes) =>
        await _timerService.StartPredefinedAsync(_settings.Current, TimeSpan.FromMinutes(minutes));

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause() => _timerService.Pause();

    [RelayCommand(CanExecute = nameof(CanResume))]
    private void Resume() => _timerService.Resume();

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _timerService.Stop();

    [RelayCommand(CanExecute = nameof(CanReset))]
    private void Reset() => _timerService.Reset();

    /// <summary>FR-005.</summary>
    [RelayCommand(CanExecute = nameof(CanSkip))]
    private void Skip() => _timerService.Skip();

    [RelayCommand]
    private void TestSound() => _audioPlayer.Play(SelectedSound?.Value, AlarmVolume);

    [RelayCommand]
    private async Task BrowseAlarm()
    {
        var path = await _filePicker.PickAudioFileAsync("Choose an alarm sound", _audioPlayer.SupportedExtensions);
        if (path is not null)
        {
            SelectedSound = ResolveSound(path);
        }
    }

    [RelayCommand]
    private async Task BrowseMusic()
    {
        var path = await _filePicker.PickAudioFileAsync("Choose music", _audioPlayer.SupportedExtensions);
        if (path is not null)
        {
            MusicPath = path;
        }
    }

    [RelayCommand]
    private void StopAudio() => _audioPlayer.Stop();

    [RelayCommand]
    private void ShowHistory() => ShowHistoryRequested?.Invoke(this, EventArgs.Empty);

    private bool CanStart() => !IsSessionActive;

    /// <summary>
    /// Available in any mode, study or break, the moment a session is running. The engine
    /// itself never restricted this — TimerEngine.Pause() only checks Idle/already-paused —
    /// so this is purely a UI choice, and Apply() already had a Study-paused status text
    /// and mode colour ready before this was ever reachable.
    /// </summary>
    private bool CanPause() => IsSessionActive && !IsPaused;

    /// <summary>
    /// Resume stays available whatever the mode: a session restored after a crash always
    /// comes back paused, and the user needs a way to pick it up again.
    /// </summary>
    private bool CanResume() => IsSessionActive && IsPaused;
    private bool CanStop() => IsSessionActive;
    private bool CanReset() => IsSessionActive;
    private bool CanSkip() => IsSessionActive;

    /// <summary>
    /// Drives the mini timer widget's visibility: shown while a session runs, hidden the
    /// moment it goes idle. Observable so App can react without polling.
    /// </summary>
    [ObservableProperty] private bool _isSessionActive;

    private bool IsPaused { get; set; }
    private TimerMode Mode { get; set; } = TimerMode.Idle;

    /// <summary>Drives the popup's Pause button visibility.</summary>
    [ObservableProperty] private bool _canPauseNow;

    [ObservableProperty] private bool _isPausedNow;

    /// <summary>0-1 through the current session, for the popup progress bar.</summary>
    [ObservableProperty] private double _sessionProgress;

    private void OnTimerUpdated(object? sender, TimerUpdatedEventArgs e) =>
        OnUiThread(() => Apply(e.State));

    private void OnSessionEnded(object? sender, SessionEndedEventArgs e)
    {
        // The history service is subscribed to the same event; refresh after it writes.
        RefreshTodaySummary();

        // Only a session that actually ran out deserves an alert. Skipping is deliberate,
        // and stopping now raises this event too so the history log sees the partial
        // session — neither should set off an alarm.
        if (e.Outcome != SessionOutcome.Completed)
        {
            return;
        }

        var (title, message) = e switch
        {
            { RunCompleted: true } => ("All sessions complete", "Nice work — that's the last one."),
            { CompletedMode: TimerMode.Study } => ("Focus session complete", "Time for a break."),
            _ => ("Break over", "Back to work.")
        };

        // Notifications and sound are platform calls, not UI-thread work, so they run
        // straight off the timer thread.
        _notificationService.ShowNotification(title, message);

        var config = _settings.Current;

        // FR-010: after a break, music replaces the alarm rather than fighting it for the
        // audio device.
        if (e.CompletedMode == TimerMode.Break
            && config.PlayMusicAfterBreak
            && !string.IsNullOrWhiteSpace(config.MusicPath))
        {
            _audioPlayer.Play(config.MusicPath, config.AlarmVolume);
            return;
        }

        _audioPlayer.Play(config.AlarmSoundPath, config.AlarmVolume);
    }

    /// <summary>
    /// Reads back today's totals from the history log. Deliberately minimal — reporting
    /// proper is a later piece of work; this exists so the stored data is visibly in use
    /// rather than write-only.
    /// </summary>
    private void RefreshTodaySummary()
    {
        var midnightLocal = new DateTimeOffset(DateTime.Today, DateTimeOffset.Now.Offset);
        var summary = _history.Summarise(midnightLocal);

        var focus = summary.TotalStudyTime;
        var text = summary.CompletedStudySessions == 0 && focus < TimeSpan.FromMinutes(1)
            ? "No sessions yet today"
            : $"Today: {(int)focus.TotalHours}h {focus.Minutes:D2}m focused · "
              + $"{summary.CompletedStudySessions} session(s) completed";

        OnUiThread(() => TodaySummary = text);
    }

    private void OnReminderDue(object? sender, ReminderDueEventArgs e)
    {
        var minutes = Math.Max(1, (int)Math.Round(e.Remaining.TotalMinutes));
        var what = e.Mode == TimerMode.Study ? "Focus session" : "Break";

        _notificationService.ShowNotification(
            $"{what} ending soon",
            $"About {minutes} minute(s) left.");

        // Play it too. The whole point of the reminder is to reach someone who is heads-down
        // and not looking at the screen, which a silent banner cannot do.
        var config = _settings.Current;
        _audioPlayer.Play(config.AlarmSoundPath, config.AlarmVolume);
    }

    /// <summary>
    /// FR-101. The engine already excludes the suspended time from the session; this just
    /// tells the user why the clock did not move while the lid was shut.
    /// </summary>
    private void OnSystemResumed(object? sender, SystemResumedEventArgs e)
    {
        var minutes = (int)Math.Round(e.SuspendedFor.TotalMinutes);
        var howLong = minutes >= 1 ? $"{minutes} min" : $"{(int)e.SuspendedFor.TotalSeconds}s";

        if (e.SessionWouldHaveEnded)
        {
            // The session did not complete: time asleep is not focus time. Say so plainly
            // in a window rather than a notification the user would have missed.
            OnUiThread(() => AlertRequested?.Invoke(this, (
                "Your session was interrupted",
                $"This machine was asleep for about {howLong}, which is longer than the "
                + "time your session had left.\n\nSleeping doesn't count as focus time, "
                + "so the session was held rather than completed. It's still waiting where "
                + "you left it.")));
            return;
        }

        OnUiThread(() => StatusText = $"Resumed after {howLong} asleep — timer was held");
    }

    /// <summary>
    /// The engine ticks on a timer thread; every property write below raises
    /// PropertyChanged, which bindings must receive on the dispatcher thread.
    /// </summary>
    private static void OnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    private void Apply(SessionState state)
    {
        CurrentTime = Format(state.RemainingTime);
        IsSessionActive = state.Mode != TimerMode.Idle;
        IsPaused = state.IsPaused;
        Mode = state.Mode;
        IsPausedNow = state.IsPaused;
        CanPauseNow = CanPause();

        var planned = state.Mode == TimerMode.Break
            ? _settings.Current.BreakDuration
            : _settings.Current.StudyDuration;
        SessionProgress = state.Mode == TimerMode.Idle || planned <= TimeSpan.Zero
            ? 0
            : Math.Clamp(1 - (state.RemainingTime.TotalSeconds / planned.TotalSeconds), 0, 1);

        ModeBrush = state.Mode switch
        {
            TimerMode.Study when state.IsPaused => ModeBrushes.Paused,
            TimerMode.Study => ModeBrushes.Study,
            TimerMode.Break when state.IsPaused => ModeBrushes.Paused,
            TimerMode.Break => ModeBrushes.Break,
            _ => ModeBrushes.Idle
        };

        var of = _settings.Current.InfiniteMode ? string.Empty : $" of {_settings.Current.SessionCount}";

        StatusText = state.Mode switch
        {
            TimerMode.Idle => "Ready",
            TimerMode.Study when state.IsPaused => $"Focus paused — session {state.CurrentSession}{of}",
            TimerMode.Study => $"Focus — session {state.CurrentSession}{of}",
            TimerMode.Break when state.IsPaused => "Break paused",
            TimerMode.Break => "On a break",
            _ => "Ready"
        };

        _trayService.UpdateStatus(new TrayStatus(
            state.Mode == TimerMode.Idle ? "Idle" : CurrentTime,
            state.Mode == TimerMode.Idle ? null : CurrentTime,
            StatusText,
            CanStart(),
            CanStart(),
            CanPause(),
            CanResume(),
            CanSkip(),
            CanReset(),
            CanStop()));

        StartCommand.NotifyCanExecuteChanged();
        StartBreakCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
        SkipCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Formats as mm:ss, rolling hours into the minutes field. TimeSpan.Minutes alone
    /// would render a 90-minute session as "30:00".
    /// </summary>
    private static string Format(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return $"{(int)value.TotalMinutes:D2}:{value.Seconds:D2}";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timerService.TimerUpdated -= OnTimerUpdated;
        _timerService.SessionEnded -= OnSessionEnded;
        _timerService.SystemResumed -= OnSystemResumed;
        _timerService.ReminderDue -= OnReminderDue;
        GC.SuppressFinalize(this);
    }
}
