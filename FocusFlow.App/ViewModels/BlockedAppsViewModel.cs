using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FocusFlow.Application.Interfaces;
using FocusFlow.Domain.Models;

namespace FocusFlow.App.ViewModels;

/// <summary>
/// Backs the dedicated "Manage Blocked Apps" window. Split out of MainWindowViewModel
/// rather than kept as a section there: a search box, a checkbox picker and a multi-select
/// list need real room to be usable, which the 420px-wide main settings window doesn't
/// have to spare — the same reasoning that already put session history in its own window.
/// </summary>
public partial class BlockedAppsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IAppBlockingService _appBlocking;

    [ObservableProperty] private string? _appSearchText;
    [ObservableProperty] private string? _manualBundleId;
    [ObservableProperty] private string? _installedAppsWarning;

    public BlockedAppsViewModel(ISettingsService settings, IAppBlockingService appBlocking)
    {
        _settings = settings;
        _appBlocking = appBlocking;

        BlockedApps = [];
        AvailableApps = [];
        FilteredApps = [];

        // So Remove selected/Remove all can disable themselves once there's nothing left
        // to act on, and so the add-apps checklist drops/regains an app the moment it's
        // blocked/unblocked — see ApplyAppFilter, which excludes whatever's in BlockedApps.
        BlockedApps.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasBlockedApps));
            ApplyAppFilter();
        };

        LoadFromSettings();

        // Fire-and-forget: the scan runs off the UI thread and populates AvailableApps
        // when it completes, so construction doesn't block on it.
        _ = RefreshInstalledApps();
    }

    /// <summary>
    /// Bundle identifiers blocked during a session, wrapped for a checkbox — plain clicks
    /// select more than one row, instead of needing Ctrl/Cmd- or Shift-click on a ListBox
    /// selection. Persisted via TimerConfig.BlockedAppIds.
    /// </summary>
    public ObservableCollection<SelectableBundleId> BlockedApps { get; }

    /// <summary>
    /// Every installed app, wrapped for the checkbox picker. The master list — stable
    /// object identity, so a checkbox stays checked across search filtering.
    /// </summary>
    public ObservableCollection<SelectableApp> AvailableApps { get; }

    /// <summary>
    /// AvailableApps filtered by AppSearchText and, importantly, with anything already in
    /// BlockedApps excluded — this is the checklist actually shown in the picker, so a
    /// blocked app can't be checked (and re-added) from here until it's unblocked again.
    /// </summary>
    public ObservableCollection<SelectableApp> FilteredApps { get; }

    /// <summary>Gates Remove selected/Remove all — nothing to remove from an empty list.</summary>
    public bool HasBlockedApps => BlockedApps.Count > 0;

    private void LoadFromSettings()
    {
        BlockedApps.Clear();
        foreach (var bundleId in _settings.Current.BlockedAppIds)
        {
            BlockedApps.Add(new SelectableBundleId(bundleId, ResolveDisplayName(bundleId)));
        }
    }

    /// <summary>
    /// Shells out to mdfind/mdls per installed app (see MacAppBlockingMonitor), which is
    /// slow enough across a few hundred apps to visibly freeze the window if run on the UI
    /// thread — so the scan itself runs on a thread-pool thread and only the resulting
    /// collection update comes back.
    /// </summary>
    [RelayCommand]
    private async Task RefreshInstalledApps()
    {
        var installed = await Task.Run(() => _appBlocking.GetInstalledApplications());

        AvailableApps.Clear();
        foreach (var app in installed.OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            AvailableApps.Add(new SelectableApp(app));
        }

        ApplyAppFilter();

        // Entries loaded from settings before this scan finished were showing their bare
        // bundle id (the only thing TimerConfig.BlockedAppIds stores) — backfill the
        // friendly name for any that turn out to be installed.
        foreach (var blocked in BlockedApps)
        {
            if (ResolveDisplayName(blocked.BundleId) is { } name)
            {
                blocked.DisplayName = name;
            }
        }

        // Zero results on a real Mac is implausible — even a fresh install has dozens of
        // apps under /System/Applications alone — so it almost certainly means mdfind
        // came back empty rather than "this machine truly has no apps," most likely
        // because Spotlight indexing is off for these folders. OperatingSystem.IsMacOS()
        // rather than _appBlocking.IsSupported: that reflects Accessibility permission,
        // which this scan doesn't need, so it'd wrongly stay silent on a real failure
        // while Accessibility also happens to be ungranted.
        InstalledAppsWarning = OperatingSystem.IsMacOS() && AvailableApps.Count == 0
            ? "Couldn't find any installed apps. Check that Spotlight indexing is enabled "
              + "for /Applications, or add one by bundle ID below."
            : null;
    }

    /// <summary>Looks up an installed app's display name by bundle id, or null if it isn't (yet) known.</summary>
    private string? ResolveDisplayName(string bundleId) =>
        AvailableApps.FirstOrDefault(a => string.Equals(a.App.BundleId, bundleId, StringComparison.OrdinalIgnoreCase))
            ?.App.DisplayName;

    /// <summary>
    /// Rebuilds FilteredApps from AvailableApps using the same SelectableApp instances, so
    /// a checkbox ticked before a search narrows the list stays ticked once it widens again.
    /// </summary>
    partial void OnAppSearchTextChanged(string? value) => ApplyAppFilter();

    private void ApplyAppFilter()
    {
        var query = AppSearchText?.Trim();
        var blockedIds = new HashSet<string>(
            BlockedApps.Select(b => b.BundleId), StringComparer.OrdinalIgnoreCase);

        var matches = AvailableApps.Where(a => !blockedIds.Contains(a.App.BundleId));
        if (!string.IsNullOrEmpty(query))
        {
            matches = matches.Where(a => a.App.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        FilteredApps.Clear();
        foreach (var app in matches)
        {
            FilteredApps.Add(app);
        }
    }

    /// <summary>
    /// Checks every currently-visible app except whichever one is frontmost right now —
    /// almost always FocusFlow itself, since this window has focus, but read fresh rather
    /// than assumed so a genuine edge case (e.g. driven without focus) still excludes
    /// whatever the user is actually using instead of blanket-blocking it too.
    /// </summary>
    [RelayCommand]
    private void SelectAllApps()
    {
        var frontmost = _appBlocking.GetFrontmostBundleId();

        foreach (var app in FilteredApps)
        {
            app.IsSelected = !string.Equals(app.App.BundleId, frontmost, StringComparison.OrdinalIgnoreCase);
        }
    }

    [RelayCommand]
    private void ClearSelectedApps()
    {
        foreach (var app in AvailableApps)
        {
            app.IsSelected = false;
        }
    }

    /// <summary>Adds every checked app to the blocked list in one go, then clears the checklist.</summary>
    [RelayCommand]
    private void AddSelectedApps()
    {
        foreach (var selectable in AvailableApps.Where(a => a.IsSelected).ToList())
        {
            AddBlockedApp(selectable.App.BundleId, selectable.App.DisplayName);
            selectable.IsSelected = false;
        }

        AppSearchText = null;
    }

    [RelayCommand]
    private void AddManualBundleId()
    {
        if (!string.IsNullOrWhiteSpace(ManualBundleId))
        {
            AddBlockedApp(ManualBundleId);
        }

        ManualBundleId = null;
    }

    /// <summary>Removes every checked row from the blocked list — one button for one or many.</summary>
    [RelayCommand]
    private void RemoveSelectedBlockedApps()
    {
        foreach (var item in BlockedApps.Where(b => b.IsSelected).ToList())
        {
            BlockedApps.Remove(item);
        }

        PersistBlockedApps();
    }

    /// <summary>Clears the whole blocked list in one click — no need to check every box first.</summary>
    [RelayCommand]
    private void RemoveAllBlockedApps()
    {
        BlockedApps.Clear();
        PersistBlockedApps();
    }

    /// <summary>
    /// The tray menu's "Block Frontmost App" quick-add. Returns the app that got blocked,
    /// or null if there was nothing to block (unsupported platform, nothing frontmost, or
    /// it's already blocked) — App.axaml.cs uses that to decide what notification to show.
    /// </summary>
    public AppInfo? BlockFrontmostApp()
    {
        var app = _appBlocking.GetFrontmostApp();
        if (app is null
            || BlockedApps.Any(b => string.Equals(b.BundleId, app.BundleId, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        AddBlockedApp(app.BundleId, app.DisplayName);
        return app;
    }

    private void AddBlockedApp(string bundleId, string? displayName = null)
    {
        var trimmed = bundleId.Trim();
        if (trimmed.Length == 0
            || BlockedApps.Any(b => string.Equals(b.BundleId, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        BlockedApps.Add(new SelectableBundleId(trimmed, displayName ?? ResolveDisplayName(trimmed)));
        PersistBlockedApps();
    }

    private void PersistBlockedApps() =>
        _settings.Update(c => c.BlockedAppIds = BlockedApps.Select(b => b.BundleId).ToList());
}
