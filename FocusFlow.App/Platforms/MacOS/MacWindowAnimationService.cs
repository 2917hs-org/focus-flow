using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using FocusFlow.App.Services;

namespace FocusFlow.App.Platforms.MacOS;

/// <summary>
/// Sets NSWindowAnimationBehaviorNone on the window's own NSWindow, which stops AppKit
/// playing its default fade/scale-in whenever the window is ordered to the front.
/// </summary>
/// <remarks>
/// Raw objc_msgSend P/Invoke, not a binding library — same rationale as
/// <c>MacAppBlockingMonitor</c>/<c>NativeMenuBarCountdown</c>. Unlike those two, this
/// checks <c>respondsToSelector:</c> before calling <c>setAnimationBehavior:</c> rather
/// than only wrapping the call in a C# try/catch for DllNotFoundException/
/// EntryPointNotFoundException: an Objective-C exception from sending an object a selector
/// it doesn't implement (confirmed the hard way — <c>Window.TryGetPlatformHandle().Handle</c>
/// was first assumed to be the content NSView, requiring an extra "get its .window" step,
/// but it is actually the AvnWindow itself, an NSWindow subclass per Avalonia's own native
/// source, and that extra step crashed the whole process) never enters the CLR's exception
/// model at all — it terminates the process outright, and no C# catch clause can stop that.
/// respondsToSelector: itself is a plain BOOL-returning message that can't throw, so it's
/// the only way to make a call whose target type isn't fully guaranteed actually safe,
/// rather than merely usually working.
/// </remarks>
public sealed class MacWindowAnimationService : IWindowAnimationService
{
    private const string ObjCLib = "/usr/lib/libobjc.dylib";

    /// <summary>NSWindowAnimationBehaviorNone.</summary>
    private static readonly IntPtr AnimationBehaviorNone = (IntPtr)2;

    public void DisableShowHideAnimation(Window window)
    {
        try
        {
            // The AvnWindow (an NSWindow subclass) itself — not its content view.
            var nsWindow = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (nsWindow == IntPtr.Zero)
            {
                return;
            }

            var selector = Sel("setAnimationBehavior:");
            if (!SendBoolFromIntPtr(nsWindow, Sel("respondsToSelector:"), selector))
            {
                return;
            }

            SendVoidFromIntPtr(nsWindow, selector, AnimationBehaviorNone);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // Best effort — see the class remarks on why this never throws outward for a
            // missing symbol. Does not, and cannot, guard against an Objective-C exception;
            // respondsToSelector: above is what does that.
        }
    }

    private static IntPtr Sel(string name) => sel_registerName(name);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendBoolFromIntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    private static extern void SendVoidFromIntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1);

    [DllImport(ObjCLib)]
    private static extern IntPtr sel_registerName(string name);
}
