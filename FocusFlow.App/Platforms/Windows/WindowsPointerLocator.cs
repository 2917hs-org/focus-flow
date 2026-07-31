using System;
using System.Runtime.InteropServices;
using Avalonia;
using FocusFlow.App.Services;

namespace FocusFlow.App.Platforms.Windows;

/// <summary>
/// FR-106 on Windows, via GetCursorPos.
/// </summary>
/// <remarks>
/// The process declares per-monitor v2 DPI awareness in app.manifest (FR-105), so
/// GetCursorPos already returns physical screen pixels — the same space as Avalonia's
/// PixelPoint. No scaling conversion is needed.
/// </remarks>
public sealed class WindowsPointerLocator : IPointerLocator
{
    public bool TryGetPosition(out PixelPoint position)
    {
        position = default;

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (!GetCursorPos(out var point))
        {
            return false;
        }

        position = new PixelPoint(point.X, point.Y);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);
}
