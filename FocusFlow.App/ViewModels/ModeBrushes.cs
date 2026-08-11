using Avalonia.Controls;
using Avalonia.Media;

namespace FocusFlow.App.ViewModels;

/// <summary>
/// Colours for the current timer mode, drawn from the active platform/theme palette
/// (Themes/MacOSPalette.axaml or Themes/WindowsPalette.axaml) rather than fixed hex values.
/// </summary>
/// <remarks>
/// Resolved on every access via <c>Avalonia.Application.Current.TryFindResource</c> rather
/// than cached, and not bound as a live DynamicResource in XAML either: <see
/// cref="MainWindowViewModel"/> assigns this as a plain C# <see cref="IBrush"/> property from
/// code (on every tick, plus once at startup) rather than from a XAML binding path a
/// DynamicResource could hook into. Resolving fresh each time — instead of caching in a
/// <c>static readonly</c> field, as this used to — matters for two reasons: a <c>static
/// readonly</c> field is initialised at first touch, which for this type happened to be
/// <em>before</em> <c>MainWindowViewModel</c>'s constructor got to apply the user's saved
/// Theme setting, so it would permanently cache whichever colour the OS's ambient
/// appearance resolved to rather than the user's actual choice; and it also means a live OS
/// theme switch while "System" is selected now picks up the new colours on the very next
/// tick, rather than needing a restart.
/// </remarks>
internal static class ModeBrushes
{
    public static IBrush Study => Resolve("PalettePrimary");

    public static IBrush Break => Resolve("PaletteSuccess");

    public static IBrush Paused => Resolve("PaletteWarning");

    /// <summary>Deliberately muted — nothing is running, so nothing should draw the eye.</summary>
    public static IBrush Idle => Resolve("PaletteSecondary");

    private static IBrush Resolve(string key)
    {
        if (Avalonia.Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Colors.Gray).ToImmutable();
    }
}
