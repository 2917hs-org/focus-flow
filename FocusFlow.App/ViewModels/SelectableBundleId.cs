using CommunityToolkit.Mvvm.ComponentModel;

namespace FocusFlow.App.ViewModels;

/// <summary>
/// Wraps a blocked bundle id with a checkbox state, so the blocked-apps list can be
/// multi-selected with plain clicks instead of Ctrl/Cmd- or Shift-click — the same
/// checkbox pattern <see cref="SelectableApp"/> already uses for the "add" picker.
/// </summary>
public sealed partial class SelectableBundleId : ObservableObject
{
    public SelectableBundleId(string bundleId, string? displayName = null)
    {
        BundleId = bundleId;
        _displayName = displayName ?? bundleId;
    }

    public string BundleId { get; }

    /// <summary>
    /// Shown in the list. Starts as the bundle id when no friendlier name is known yet —
    /// settable rather than computed once, so BlockedAppsViewModel can backfill it once
    /// the installed-apps scan finishes resolving names.
    /// </summary>
    [ObservableProperty] private string _displayName;

    [ObservableProperty] private bool _isSelected;
}
