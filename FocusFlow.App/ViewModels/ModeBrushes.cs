using Avalonia.Controls;
using Avalonia.Media;

namespace FocusFlow.App.ViewModels;

/// <summary>
/// Colours for the current timer mode, drawn from the active platform/theme palette
/// (Themes/MacOSPalette.axaml or Themes/WindowsPalette.axaml) rather than fixed hex values.
/// </summary>
/// <remarks>
/// Resolved once, on first access, via <c>Avalonia.Application.Current.TryFindResource</c> rather
/// than bound as a live DynamicResource in XAML: <see cref="MainWindowViewModel"/> assigns
/// this as a plain C# <see cref="IBrush"/> property once a second from code, not from a
/// XAML binding path a DynamicResource could hook into. That first access happens after
/// <c>App.Initialize()</c> has already merged the palette into <c>Application.Resources</c>
/// — MainWindowViewModel is constructed from the DI container in
/// <c>OnFrameworkInitializationCompleted</c>, which always runs after <c>Initialize()</c> —
/// so the resource is there by the time anything asks for it. The one real limitation this
/// carries: a live OS theme switch while "System" is selected and the app is already
/// running won't re-resolve these particular colours until restart, unlike everything
/// still wired through DynamicResource in XAML.
/// </remarks>
internal static class ModeBrushes
{
    public static readonly IBrush Study = Resolve("PalettePrimary");

    public static readonly IBrush Break = Resolve("PaletteSuccess");

    public static readonly IBrush Paused = Resolve("PaletteWarning");

    /// <summary>Deliberately muted — nothing is running, so nothing should draw the eye.</summary>
    public static readonly IBrush Idle = Resolve("PaletteSecondary");

    private static IBrush Resolve(string key)
    {
        if (Avalonia.Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Colors.Gray).ToImmutable();
    }
}
