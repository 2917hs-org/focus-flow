using CommunityToolkit.Mvvm.ComponentModel;
using FocusFlow.Domain.Models;

namespace FocusFlow.App.ViewModels;

/// <summary>
/// Wraps an installed app with a checkbox state for the multi-select "Add blocked apps"
/// picker. AppInfo itself is an immutable Domain record with no INotifyPropertyChanged, so
/// the checkbox needs something to bind IsChecked against.
/// </summary>
public sealed partial class SelectableApp : ObservableObject
{
    public SelectableApp(AppInfo app)
    {
        App = app;
    }

    public AppInfo App { get; }

    [ObservableProperty] private bool _isSelected;
}
