using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Data.Converters;
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

/// <summary>
/// Words a user would actually read for each <see cref="HistoryRange"/> member, for the
/// range ComboBox's ItemTemplate in HistoryView.axaml — enum names are for code, and
/// "ThisWeek" rendered as the ComboBox's own ToString() fallback rather than "This Week".
/// </summary>
public static class HistoryRangeDisplay
{
    public static readonly IValueConverter Converter =
        new FuncValueConverter<HistoryRange, string>(range => range switch
        {
            HistoryRange.Today => "Today",
            HistoryRange.ThisWeek => "This Week",
            HistoryRange.ThisMonth => "This Month",
            HistoryRange.AllTime => "All Time",
            _ => range.ToString()
        });
}

/// <summary>One row in the history list — pre-formatted so the view needs no converters.</summary>
public sealed record HistoryEntry(string When, string Mode, string Outcome, string Duration, string? Label)
{
    /// <summary>Drives the label row's visibility — kept here rather than in the view so no converter is needed.</summary>
    public bool HasLabel => !string.IsNullOrWhiteSpace(Label);
}

/// <summary>
/// One bar in the daily-minutes chart. <see cref="BarHeight"/> is pre-computed in pixels
/// (rather than left as a raw fraction) for the same reason <see cref="HistoryEntry"/> is
/// pre-formatted — the view binds it straight to a Rectangle's Height with no converter.
/// </summary>
public sealed record DailyChartBar(string DayLabel, double Minutes, double BarHeight);

/// <summary>One row in the "by label" breakdown — busiest label first, see <see cref="LabelTotal"/>.</summary>
public sealed record LabelBreakdownEntry(string Label, string Duration, int SessionCount)
{
    public string Summary => SessionCount == 1 ? $"{Duration} · 1 session" : $"{Duration} · {SessionCount} sessions";
}

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

    /// <summary>Sentinel item in <see cref="AvailableLabelFilters"/> meaning "don't filter".</summary>
    private const string AllLabelsOption = "All sessions";

    /// <summary>
    /// Sentinel item in <see cref="AvailableLabelFilters"/> for the unlabelled bucket — a
    /// real label can't collide with it since <see cref="SessionRecord.Label"/> is trimmed
    /// and null/blank before it's ever saved.
    /// </summary>
    private const string NoLabelOption = "No label";

    private readonly SessionHistoryService _history;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// True while <see cref="Refresh"/> is rebuilding <see cref="AvailableLabelFilters"/> —
    /// clearing and repopulating that collection can bounce the label ComboBox's SelectedItem
    /// through a transient null, and without this guard that would re-enter Refresh.
    /// </summary>
    private bool _refreshing;

    [ObservableProperty] private HistoryRange _selectedRange = HistoryRange.ThisWeek;
    [ObservableProperty] private string _selectedLabelFilter = AllLabelsOption;
    [ObservableProperty] private string _summaryText = string.Empty;
    [ObservableProperty] private string _streakText = string.Empty;
    [ObservableProperty] private bool _hasEntries;
    [ObservableProperty] private bool _hasChartData;
    [ObservableProperty] private bool _hasLabelBreakdown;

    public HistoryViewModel(SessionHistoryService history, TimeProvider timeProvider)
    {
        _history = history;
        _timeProvider = timeProvider;
        Refresh();
    }

    public IReadOnlyList<HistoryRange> AvailableRanges { get; } =
        [HistoryRange.Today, HistoryRange.ThisWeek, HistoryRange.ThisMonth, HistoryRange.AllTime];

    /// <summary>
    /// "All sessions" plus every distinct label in <see cref="SelectedRange"/>, busiest
    /// first — rebuilt on every <see cref="Refresh"/> since which labels exist depends on
    /// the range.
    /// </summary>
    public ObservableCollection<string> AvailableLabelFilters { get; } = [AllLabelsOption];

    /// <summary>Newest first — matches <see cref="SessionHistoryService.GetRecords"/>.</summary>
    public ObservableCollection<HistoryEntry> Entries { get; } = [];

    /// <summary>
    /// Oldest first, left to right — independent of <see cref="Entries"/>' newest-first
    /// order, which is for scanning a list rather than reading a timeline.
    /// </summary>
    public ObservableCollection<DailyChartBar> ChartBars { get; } = [];

    /// <summary>
    /// Total study time per label in <see cref="SelectedRange"/> — always the whole range,
    /// independent of <see cref="SelectedLabelFilter"/>, so this answers "where did my time
    /// go" while the filter narrows the list below to "show me those sessions".
    /// </summary>
    public ObservableCollection<LabelBreakdownEntry> LabelBreakdown { get; } = [];

    partial void OnSelectedRangeChanged(HistoryRange value) => Refresh();

    partial void OnSelectedLabelFilterChanged(string value)
    {
        OnPropertyChanged(nameof(EmptyStateText));

        if (_refreshing)
        {
            return;
        }

        Refresh();
    }

    /// <summary>
    /// Also a command rather than only an internal method: there is no live-update wiring
    /// to <see cref="Application.Interfaces.ITimerService.SessionEnded"/> here — this is a
    /// secondary, occasionally-opened window, not the main timer display — so a manual
    /// refresh is how a session that just finished shows up without closing and reopening.
    /// </summary>
    [RelayCommand]
    private void Refresh()
    {
        _refreshing = true;
        try
        {
            var since = Since(SelectedRange);
            var summary = _history.Summarise(since);
            var allRecords = _history.GetRecords(since);
            var labelTotals = _history.LabelTotalsSince(since);

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

            // Clearing AvailableLabelFilters below drops the ComboBox's current selection —
            // SelectedItem is two-way bound, so Avalonia pushes that loss straight back into
            // SelectedLabelFilter as null. Restored explicitly once the collection is rebuilt,
            // rather than left for the ComboBox to sort out on its own, which is what used to
            // leave the filter permanently blank after the first Refresh().
            var previousFilter = SelectedLabelFilter;

            AvailableLabelFilters.Clear();
            AvailableLabelFilters.Add(AllLabelsOption);
            LabelBreakdown.Clear();
            foreach (var total in labelTotals)
            {
                AvailableLabelFilters.Add(total.Label ?? NoLabelOption);
                LabelBreakdown.Add(new LabelBreakdownEntry(
                    total.Label ?? NoLabelOption, FormatDuration(total.TotalTime), total.SessionCount));
            }

            SelectedLabelFilter = AvailableLabelFilters.Contains(previousFilter)
                ? previousFilter
                : AllLabelsOption;

            // A lone "No label" row would just restate the summary above in a second card —
            // only worth showing once there's an actual label to compare it against.
            HasLabelBreakdown = labelTotals.Any(t => t.Label is not null);

            var filtered = SelectedLabelFilter switch
            {
                AllLabelsOption => allRecords,
                NoLabelOption => allRecords.Where(r => string.IsNullOrWhiteSpace(r.Label)).ToList(),
                var label => allRecords.Where(r =>
                    string.Equals(r.Label, label, StringComparison.OrdinalIgnoreCase)).ToList()
            };

            Entries.Clear();
            foreach (var record in filtered)
            {
                Entries.Add(new HistoryEntry(
                    FormatWhen(record.EndedAt),
                    record.Mode == TimerMode.Study ? "Focus" : "Break",
                    record.Outcome.ToString(),
                    FormatDuration(record.ActualDuration),
                    record.Label));
            }

            HasEntries = Entries.Count > 0;
            OnPropertyChanged(nameof(NoEntries));

            RefreshChart(since);
        }
        finally
        {
            _refreshing = false;
        }
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

    /// <summary>
    /// Distinguishes "nothing in range" from "nothing under this label" so the label
    /// filter narrowing the list to zero doesn't read as the log being broken.
    /// </summary>
    public string EmptyStateText => SelectedLabelFilter == AllLabelsOption
        ? "No sessions in this range."
        : "No sessions match this filter.";

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
