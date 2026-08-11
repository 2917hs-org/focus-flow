using Avalonia.Controls;
using FocusFlow.App.Services;

namespace FocusFlow.App.Platforms.Windows;

/// <summary>
/// Windows doesn't play the same unwanted show/hide animation macOS does for these windows
/// — see MacWindowAnimationService — so there's nothing to suppress here.
/// </summary>
public sealed class NoopWindowAnimationService : IWindowAnimationService
{
    public void DisableShowHideAnimation(Window window)
    {
    }
}
