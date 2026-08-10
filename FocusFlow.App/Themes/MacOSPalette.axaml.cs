using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FocusFlow.App.Themes;

/// <summary>
/// The macOS colour palette, merged into <c>Application.Resources</c> at startup when
/// <c>OperatingSystem.IsMacOS()</c>. A resource-dictionary-per-file (mirroring how
/// <c>App.axaml</c>/<c>App.axaml.cs</c> itself works) rather than building it in code: the
/// XAML compiler already handles Light/Dark <c>ThemeDictionaries</c> switching, so there's
/// nothing to reimplement.
/// </summary>
public sealed partial class MacOSPalette : ResourceDictionary
{
    public MacOSPalette()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
