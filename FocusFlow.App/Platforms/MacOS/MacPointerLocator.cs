using System;
using System.Runtime.InteropServices;
using Avalonia;
using FocusFlow.App.Services;

namespace FocusFlow.App.Platforms.MacOS;

/// <summary>
/// FR-106 on macOS, via CoreGraphics.
/// </summary>
/// <remarks>
/// CGEventGetLocation reports global coordinates in points with the origin at the
/// top-left of the main display, which matches Avalonia's PixelPoint orientation but not
/// its units — Avalonia works in physical pixels. The point is therefore scaled by the
/// main display's backing factor. That is exact whenever every display shares a scale
/// factor (the overwhelmingly common case, including all-Retina and all-standard setups);
/// on a mixed Retina/non-Retina layout it can pick the neighbouring display, in which
/// case the window simply opens on the wrong monitor rather than misbehaving.
/// </remarks>
public sealed class MacPointerLocator : IPointerLocator
{
    private const string CoreGraphics =
        "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    public bool TryGetPosition(out PixelPoint position)
    {
        position = default;

        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        var handle = IntPtr.Zero;
        try
        {
            handle = CGEventCreate(IntPtr.Zero);
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            var location = CGEventGetLocation(handle);
            var scale = MainDisplayScale();

            position = new PixelPoint(
                (int)Math.Round(location.X * scale),
                (int)Math.Round(location.Y * scale));
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                CFRelease(handle);
            }
        }
    }

    /// <summary>Backing-store factor of the main display: 2 on Retina, 1 otherwise.</summary>
    private static double MainDisplayScale()
    {
        try
        {
            var display = CGMainDisplayID();
            var pixels = CGDisplayPixelsWide(display);
            var mode = CGDisplayCopyDisplayMode(display);

            if (mode == IntPtr.Zero || pixels == 0)
            {
                return 1.0;
            }

            try
            {
                var native = CGDisplayModeGetPixelWidth(mode);
                return native > 0 ? native / (double)pixels : 1.0;
            }
            finally
            {
                CGDisplayModeRelease(mode);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return 1.0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint
    {
        public double X;
        public double Y;
    }

    [DllImport(CoreGraphics)]
    private static extern IntPtr CGEventCreate(IntPtr source);

    [DllImport(CoreGraphics)]
    private static extern CGPoint CGEventGetLocation(IntPtr @event);

    [DllImport(CoreGraphics)]
    private static extern uint CGMainDisplayID();

    [DllImport(CoreGraphics)]
    private static extern nuint CGDisplayPixelsWide(uint display);

    [DllImport(CoreGraphics)]
    private static extern IntPtr CGDisplayCopyDisplayMode(uint display);

    [DllImport(CoreGraphics)]
    private static extern nuint CGDisplayModeGetPixelWidth(IntPtr mode);

    [DllImport(CoreGraphics)]
    private static extern void CGDisplayModeRelease(IntPtr mode);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr cf);
}
