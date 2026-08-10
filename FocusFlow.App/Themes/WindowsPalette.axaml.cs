using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FocusFlow.App.Themes;

/// <summary>
/// The Windows colour palette — see <see cref="MacOSPalette"/>'s remarks for why this is
/// a resource-dictionary-per-file rather than built in code.
/// </summary>
public sealed partial class WindowsPalette : ResourceDictionary
{
    public WindowsPalette()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
