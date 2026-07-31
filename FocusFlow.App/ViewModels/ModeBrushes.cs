using Avalonia.Media;

namespace FocusFlow.App.ViewModels;

/// <summary>
/// Colours for the current timer mode, matching the semantic button roles in App.axaml.
/// </summary>
/// <remarks>
/// Frozen singletons rather than new brushes per tick: <see cref="MainWindowViewModel"/>
/// reassigns this once a second, and allocating a brush each time would churn for no
/// reason.
/// </remarks>
internal static class ModeBrushes
{
    /// <summary>Indigo, matching the app icon.</summary>
    public static readonly IBrush Study = Freeze(Color.FromRgb(0x4F, 0x46, 0xE5));

    public static readonly IBrush Break = Freeze(Color.FromRgb(0x2E, 0x9E, 0x5B));

    public static readonly IBrush Paused = Freeze(Color.FromRgb(0xC7, 0x77, 0x00));

    /// <summary>Deliberately grey — nothing is running, so nothing should draw the eye.</summary>
    public static readonly IBrush Idle = Freeze(Color.FromRgb(0x80, 0x80, 0x80));

    private static IBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        return brush.ToImmutable();
    }
}
