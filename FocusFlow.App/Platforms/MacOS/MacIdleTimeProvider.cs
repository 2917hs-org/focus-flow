using System;
using System.Runtime.InteropServices;
using FocusFlow.Application.Interfaces;

namespace FocusFlow.App.Platforms.MacOS;

/// <summary>
/// Idle-auto-pause support on macOS, via CoreGraphics.
/// </summary>
/// <remarks>
/// CGEventSourceSecondsSinceLastEventType reports how long it has been since the HID
/// system last saw any keyboard or mouse event, regardless of which app (if any) is
/// focused — the same framework <see cref="MacPointerLocator"/> already calls, a
/// different function.
/// </remarks>
public sealed class MacIdleTimeProvider : IIdleTimeProvider
{
    private const string CoreGraphics =
        "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    /// <summary>kCGEventSourceStateHIDSystemState.</summary>
    private const int HidSystemState = 1;

    /// <summary>kCGAnyInputEventType.</summary>
    private const uint AnyInputEventType = unchecked((uint)~0);

    public TimeSpan? GetIdleTime()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        try
        {
            var seconds = CGEventSourceSecondsSinceLastEventType(HidSystemState, AnyInputEventType);
            return seconds < 0 ? null : TimeSpan.FromSeconds(seconds);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [DllImport(CoreGraphics)]
    private static extern double CGEventSourceSecondsSinceLastEventType(int stateID, uint eventType);
}
