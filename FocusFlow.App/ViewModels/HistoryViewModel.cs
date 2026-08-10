using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
/// Reads back what <see cref="SessionHistoryService"/> has been logging all along.
/// <see cref="SessionHistoryService"/>'s own doc comment already says reporting was meant
/// to be built on top of its store later — this is that later.
/// </summary>
public partial class HistoryViewModel : ObservableObject
{
    private readonly SessionHistoryService _history;
    private readonly TimeProvider _timeProvider;

    [ObservableProperty] private HistoryRange _selectedRange = HistoryRange.ThisWeek;
    [ObservableProperty] private string _summaryText = string.Empty;
    [ObservableProperty] private bool _hasEntries;

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
