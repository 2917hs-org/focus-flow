using System;
using System.Runtime.InteropServices;
using FocusFlow.Application.Interfaces;

namespace FocusFlow.App.Platforms.MacOS;

/// <summary>
/// A second, native NSStatusItem that shows the countdown as real menu bar text.
/// </summary>
/// <remarks>
/// <para>
/// The countdown used to be rendered into a bitmap and set as the tray icon's image —
/// Avalonia's TrayIcon has no API for text beside a status item, only an image. That broke
/// once the readout widened from an abbreviated "25m" to a full "44:54": NSStatusItem
/// doesn't reliably scale an oversized image down to whatever room is actually left in a
/// crowded menu bar, it clips it, which is what produced "4:5" out of "44:54". A status
/// item given a real text title doesn't have this problem — AppKit sizes the item to the
/// text itself, the same mechanism the system's own menu bar Stopwatch relies on.
/// </para>
/// <para>
/// Deliberately a second, additive status item rather than a replacement for Avalonia's
/// own TrayIcon: that one still owns the app icon and the entire NativeMenu
/// (Start/Pause/Skip/…), which would be a far larger and riskier rewrite to reproduce by
/// hand over the Objective-C runtime. This one is a pure text readout with no menu of its
/// own — clicking it does nothing; the icon right next to it still opens the real menu.
/// </para>
/// <para>
/// Raw objc_msgSend P/Invoke calls, not a binding library: FocusFlow doesn't otherwise
/// depend on Xamarin.Mac/MonoMac, and pulling one in for one status item's text would be a
/// much bigger dependency than a handful of DllImport declarations. Every call is wrapped
/// in the same defensive try/catch MacPointerLocator and MacStartupService already use for
/// native interop — a missing symbol degrades to "no readout," never a crash.
/// </para>
/// </remarks>
public sealed class NativeMenuBarCountdown : IMenuBarCountdown, IDisposable
{
    private const string ObjCLib = "/usr/lib/libobjc.dylib";

    /// <summary>NSVariableStatusItemLength — AppKit sizes the item to its content.</summary>
    private const double VariableLength = -1;

    private readonly IntPtr _statusItem;
    private readonly IntPtr _button;
    private readonly bool _available;
    private string? _lastText;
    private bool _disposed;

    public NativeMenuBarCountdown()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            var statusBarClass = objc_getClass("NSStatusBar");
            var systemStatusBar = SendIntPtr(statusBarClass, Sel("systemStatusBar"));
            _statusItem = SendIntPtrFromDouble(systemStatusBar, Sel("statusItemWithLength:"), VariableLength);
            _button = SendIntPtr(_statusItem, Sel("button"));

            // Monospaced digits so the menu bar doesn't jiggle sideways as the numbers
            // change — the same NSFont API the system's own numeric status items use.
            var fontClass = objc_getClass("NSFont");
            var font = SendIntPtrFromTwoDoubles(fontClass, Sel("monospacedDigitSystemFontOfSize:weight:"), 13, 0);
            SendVoidWithIntPtr(_button, Sel("setFont:"), font);

            SendVoidWithBool(_statusItem, Sel("setVisible:"), false);
            _available = _statusItem != IntPtr.Zero && _button != IntPtr.Zero;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _available = false;
        }
    }

    public void SetVisible(bool visible)
    {
        if (!_available)
        {
            return;
        }

        try
        {
            SendVoidWithBool(_statusItem, Sel("setVisible:"), visible);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // Best effort — see the class remarks on why this never throws outward.
        }
    }

    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!_available || text == _lastText)
        {
            return;
        }

        try
        {
            var nsString = SendIntPtrFromUtf8(objc_getClass("NSString"), Sel("stringWithUTF8String:"), text);
            SendVoidWithIntPtr(_button, Sel("setTitle:"), nsString);
            _lastText = text;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // Best effort — see the class remarks on why this never throws outward.
        }
    }

    public void Dispose()
    {
        if (!_available || _disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            var statusBarClass = objc_getClass("NSStatusBar");
            var systemStatusBar = SendIntPtr(statusBarClass, Sel("systemStatusBar"));
            SendVoidWithIntPtr(systemStatusBar, Sel("removeStatusItem:"), _statusItem);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // The process is exiting either way; nothing left to hand this off to.
        }
    }

    private static IntPtr Sel(string name) => sel_registerName(name);

    // objc_msgSend has no fixed signature — the Objective-C runtime dispatches on whatever
    // arguments the caller actually passes, so each distinct call shape below needs its own
    // DllImport entry point sharing the same native symbol. This is the standard technique
    // for calling into AppKit without a binding library; the .NET JIT emits the calling
    // convention each overload's own parameter types describe, which is what makes it work.
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendIntPtr(IntPtr receiver, IntPtr selector);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendIntPtrFromDouble(IntPtr receiver, IntPtr selector, double arg1);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendIntPtrFromTwoDoubles(IntPtr receiver, IntPtr selector, double arg1, double arg2);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendIntPtrFromUtf8(
        IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg1);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    private static extern void SendVoidWithIntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    private static extern void SendVoidWithBool(
        IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool arg1);

    [DllImport(ObjCLib)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjCLib)]
    private static extern IntPtr sel_registerName(string name);
}
