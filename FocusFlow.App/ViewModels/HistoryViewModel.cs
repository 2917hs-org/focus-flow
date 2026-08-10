using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FocusFlow.Application.Services;
using FocusFlow.Domain.Models;

namespace FocusFlow.App.ViewModels;

public enum HistoryRange
{
    Today,
    ThisWeek,
    ThisMonth,
    AllTime
}

/// <summary>One row in the history list — pre-formatted so the view needs no converters.</summary>
public sealed record HistoryEntry(string When, string Mode, string Outcome, string Duration);

/// <summary>
/// One bar in the daily-minutes chart. <see cref="BarHeight"/> is pre-computed in pixels
/// (rather than left as a raw fraction) for the same reason <see cref="HistoryEntry"/> is
/// pre-formatted — the view binds it straight to a Rectangle's Height with no converter.
/// </summary>
public sealed record DailyChartBar(string DayLabel, double Minutes, double BarHeight);

/// <summary>
/// Reads back what <see cref="SessionHistoryService"/> has been logging all along.
/// <see cref="SessionHistoryService"/>'s own doc comment already says reporting was meant
/// to be built on top of its store later — this is that later.
/// </summary>
public partial class HistoryViewModel : ObservableObject
{
    /// <summary>
    /// Tallest bar in the chart, in pixels — bars are scaled relative to the range's
    /// busiest day so the chart always uses the full height available to it.
    /// </summary>
    private const double MaxBarHeight = 72;

    private readonly SessionHistoryService _history;
    private readonly TimeProvider _timeProvider;

    [ObservableProperty] private HistoryRange _selectedRange = HistoryRange.ThisWeek;
    [ObservableProperty] private string _summaryText = string.Empty;
    [ObservableProperty] private string _streakText = string.Empty;
    [ObservableProperty] private bool _hasEntries;
    [ObservableProperty] private bool _hasChartData;

    public HistoryViewModel(SessionHistoryService history, TimeProvider timeProvider)
    {
        _history = history;
        _timeProvider = timeProvider;
        Refresh();
    }

    public IReadOnlyList<HistoryRange> AvailableRanges { get; } =
        [HistoryRange.Today, HistoryRange.ThisWeek, HistoryRange.ThisMonth, HistoryRange.AllTime];

    /// <summary>Newest first — matches <see cref="SessionHistoryService.GetRecords"/>.</summary>
    public ObservableCollection<HistoryEntry> Entries { get; } = [];

    /// <summary>
    /// Oldest first, left to right — independent of <see cref="Entries"/>' newest-first
    /// order, which is for scanning a list rather than reading a timeline.
    /// </summary>
    public ObservableCollection<DailyChartBar> ChartBars { get; } = [];

    partial void OnSelectedRangeChanged(HistoryRange value) => Refresh();

    /// <summary>
    /// Also a command rather than only an internal method: there is no live-update wiring
    /// to <see cref="Application.Interfaces.ITimerService.SessionEnded"/> here — this is a
    /// secondary, occasionally-opened window, not the main timer display — so a manual
    /// refresh is how a session that just finished shows up without closing and reopening.
    /// </summary>
    [RelayCommand]
    private void Refresh()
    {
        var since = Since(SelectedRange);
        var summary = _history.Summarise(since);
        var records = _history.GetRecords(since);

        SummaryText = summary.CompletedStudySessions == 0 && summary.TotalStudyTime == TimeSpan.Zero
            ? "No sessions in this range"
            : $"{FormatDuration(summary.TotalStudyTime)} focused · "
              + $"{FormatDuration(summary.TotalBreakTime)} on break · "
              + $"{summary.CompletedStudySessions} session(s) completed";

        // The streak is a global fact about the log, not scoped to SelectedRange — it
        // wouldn't make sense for "your streak" to change depending on which filter is
        // showing.
        var streak = _history.CurrentStreak();
        StreakText = streak switch
        {
            0 => "No active streak",
            1 => "Current streak: 1 day",
            _ => $"Current streak: {streak} days"
        };

        Entries.Clear();
        foreach (var record in records)
        {
            Entries.Add(new HistoryEntry(
                FormatWhen(record.EndedAt),
                record.Mode == TimerMode.Study ? "Focus" : "Break",
                record.Outcome.ToString(),
                FormatDuration(record.ActualDuration)));
        }

        HasEntries = Entries.Count > 0;
        OnPropertyChanged(nameof(NoEntries));

        RefreshChart(since);
    }

    /// <summary>
    /// Fills in zero-minute days between the range's start and today so the chart reads
    /// as an evenly-spaced timeline rather than only the days something happened.
    /// </summary>
    private void RefreshChart(DateTimeOffset? since)
    {
        ChartBars.Clear();

        var daily = _history.DailyFocusMinutesSince(since);
        if (daily.Count == 0)
        {
            HasChartData = false;
            return;
        }

        var byDay = daily.ToDictionary(d => d.Day, d => d.Minutes);
        var startDay = since.HasValue ? DateOnly.FromDateTime(since.Value.Date) : daily[0].Day;
        var endDay = DateOnly.FromDateTime(_timeProvider.GetLocalNow().Date);
        var maxMinutes = daily.Max(d => d.Minutes);

        for (var day = startDay; day <= endDay; day = day.AddDays(1))
        {
            var minutes = byDay.GetValueOrDefault(day);
            var barHeight = minutes <= 0 ? 0 : Math.Max(4, minutes / maxMinutes * MaxBarHeight);
            ChartBars.Add(new DailyChartBar(day.ToString("MMM d"), minutes, barHeight));
        }

        HasChartData = true;
    }

    /// <summary>
    /// A separate computed property rather than a "!HasEntries" binding in the view: it
    /// keeps the XAML side to plain property bindings only, matching how the rest of this
    /// app avoids binding-expression syntax that isn't already proven to work here.
    /// </summary>
    public bool NoEntries => !HasEntries;

    /// <summary>Null for <see cref="HistoryRange.AllTime"/> — matches the store's "null = everything".</summary>
    private DateTimeOffset? Since(HistoryRange range)
    {
        var now = _timeProvider.GetLocalNow();

        return range switch
        {
            HistoryRange.Today => new DateTimeOffset(now.Date, now.Offset),
            HistoryRange.ThisWeek => new DateTimeOffset(now.Date.AddDays(-(int)now.DayOfWeek), now.Offset),
            HistoryRange.ThisMonth => new DateTimeOffset(new DateTime(now.Year, now.Month, 1), now.Offset),
            _ => null
        };
    }

    private static string FormatWhen(DateTimeOffset endedAtUtc) =>
        endedAtUtc.ToLocalTime().ToString("MMM d, h:mm tt");

    private static string FormatDuration(TimeSpan value) =>
        value.TotalHours >= 1
            ? $"{(int)value.TotalHours}h {value.Minutes:D2}m"
            : $"{value.Minutes}m {value.Seconds:D2}s";
}
