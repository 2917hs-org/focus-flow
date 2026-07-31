using System;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace FocusFlow.App.Services;

/// <summary>
/// Draws the countdown into a bitmap so it can be shown as the tray icon itself.
/// </summary>
/// <remarks>
/// <para>
/// macOS has no API Avalonia exposes for putting text beside a status item, but the icon
/// is just an image — so rendering "24:35" as the image puts the countdown in the menu bar
/// without any native interop.
/// </para>
/// <para>
/// Rendered black on transparent and flagged as a template image, which is what lets macOS
/// invert it automatically for a light or dark menu bar. A fixed colour would be invisible
/// in one of the two.
/// </para>
/// </remarks>
public static class TrayIconRenderer
{
    // Two or three characters, so this can be far larger than an mm:ss string allowed.
    private const double FontSize = 19;
    private const double PaddingX = 2;

    /// <summary>
    /// A hair of vertical breathing room. The bitmap otherwise hugs the glyphs, which is
    /// the point: macOS scales a status-item image to the menu bar height, so any padding
    /// baked in here is height the digits do not get to use.
    /// </summary>
    private const double PaddingY = 1;

    /// <summary>Rendered at 3x so downscaling into the menu bar stays crisp.</summary>
    private const double Scale = 3;

    /// <summary>
    /// Monospaced so the icon keeps a constant width as the digits change. A proportional
    /// face makes the whole menu bar shuffle sideways every second.
    /// </summary>
    private static readonly FontFamily Face = FontFamily.Parse("Menlo, Consolas, monospace");

    public static RenderTargetBitmap Render(string text)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(Face, FontStyle.Normal, FontWeight.SemiBold),
            FontSize,
            Brushes.Black);

        var width = formatted.Width + (PaddingX * 2);
        var height = formatted.Height + (PaddingY * 2);

        var bitmap = new RenderTargetBitmap(
            new PixelSize((int)Math.Ceiling(width * Scale), (int)Math.Ceiling(height * Scale)),
            new Vector(96 * Scale, 96 * Scale));

        using (var context = bitmap.CreateDrawingContext())
        {
            context.DrawText(formatted, new Point(PaddingX, PaddingY));
        }

        return bitmap;
    }
}
