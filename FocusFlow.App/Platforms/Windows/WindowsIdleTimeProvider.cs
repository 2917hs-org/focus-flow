using System;
using System.Runtime.InteropServices;
using FocusFlow.Application.Interfaces;

namespace FocusFlow.App.Platforms.Windows;

/// <summary>
/// Idle-auto-pause support on Windows, via GetLastInputInfo.
/// </summary>
public sealed class WindowsIdleTimeProvider : IIdleTimeProvider
{
    public TimeSpan? GetIdleTime()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
        {
            return null;
        }

        // Both dwTime and Environment.TickCount are 32-bit millisecond counters that wrap
        // around every ~49.7 days; the unchecked subtraction still yields the correct
        // (small, non-negative) gap across that rollover.
        var idleMilliseconds = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(idleMilliseconds);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);
}
