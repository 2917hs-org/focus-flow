using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FocusFlow.Application.Interfaces;
using FocusFlow.Domain.Models;

namespace FocusFlow.App.Platforms.MacOS;

/// <summary>
/// User-configurable global keyboard shortcuts (see README for the defaults, and Settings
/// for changing them) via Carbon's RegisterEventHotKey/InstallEventHandler.
/// </summary>
/// <remarks>
/// <para>
/// Carbon's hotkey API, deliberately not an NSEvent global monitor
/// (<c>addGlobalMonitorForEventsMatchingMask:</c>): a global NSEvent monitor requires
/// Accessibility permission, and nothing here needs to ask for that just to start or pause
/// a timer. RegisterEventHotKey needs no permission at all — it's the same mechanism macOS
/// itself uses for things like Spotlight's ⌘Space.
/// </para>
/// <para>
/// Registered against <c>GetApplicationEventTarget()</c>, which delivers hot-key events
/// through the same main run loop Avalonia's Cocoa backend already pumps for the app's own
/// windows and menus — no extra run-loop wiring needed, the same reason a plain Cocoa app
/// gets this for free.
/// </para>
/// <para>
/// Only letters and digits are supported keys (see <see cref="CarbonKeyCodes"/>) — Settings
/// never offers anything else to capture, which keeps this lookup table small and
/// verifiable rather than risking a mis-transcribed keycode for a rarely-used key.
/// </para>
/// <para>
/// Raw objc-adjacent P/Invoke into Carbon.framework, matching
/// <see cref="NativeMenuBarCountdown"/> and <see cref="MacAppBlockingMonitor"/>: every call
/// is wrapped in the same defensive try/catch, so a missing symbol degrades to "no
/// hotkeys," never a crash.
/// </para>
/// </remarks>
public sealed class MacGlobalHotkeys : IGlobalHotkeys
{
    private const string CarbonLib = "/System/Library/Frameworks/Carbon.framework/Carbon";

    // Carbon event modifier masks (Carbon/Events.h).
    private const uint CmdKey = 0x0100;
    private const uint ShiftKey = 0x0200;
    private const uint OptionKey = 0x0800;
    private const uint ControlKey = 0x1000;

    private const uint StartPauseId = 1;
    private const uint StopId = 2;
    private const uint SkipId = 3;

    private const uint EventHotKeyPressed = 5;

    private static readonly uint EventClassKeyboard = FourCC("keyb");
    private static readonly uint TypeEventHotKeyId = FourCC("hkid");
    private static readonly uint ParamDirectObject = FourCC("----");
    private static readonly uint HotKeySignature = FourCC("FFKH");

    /// <summary>
    /// Standard Carbon virtual keycodes (Events.h), keyed by the exact Avalonia Key enum
    /// member name Settings' capture control can produce.
    /// </summary>
    private static readonly Dictionary<string, uint> CarbonKeyCodes = new()
    {
        ["A"] = 0, ["B"] = 11, ["C"] = 8, ["D"] = 2, ["E"] = 14, ["F"] = 3, ["G"] = 5,
        ["H"] = 4, ["I"] = 34, ["J"] = 38, ["K"] = 40, ["L"] = 37, ["M"] = 46, ["N"] = 45,
        ["O"] = 31, ["P"] = 35, ["Q"] = 12, ["R"] = 15, ["S"] = 1, ["T"] = 17, ["U"] = 32,
        ["V"] = 9, ["W"] = 13, ["X"] = 7, ["Y"] = 16, ["Z"] = 6,
        ["D0"] = 29, ["D1"] = 18, ["D2"] = 19, ["D3"] = 20, ["D4"] = 21,
        ["D5"] = 23, ["D6"] = 22, ["D7"] = 26, ["D8"] = 28, ["D9"] = 25
    };

    private readonly object _applyLock = new();
    private readonly EventHandlerProc _handler;
    private readonly bool _isMacOs;
    private bool _available;
    private IntPtr _target;
    private IntPtr _handlerRef;
    private IntPtr _startPauseRef;
    private IntPtr _stopRef;
    private IntPtr _skipRef;
    private bool _disposed;

    public event EventHandler? StartPauseRequested;
    public event EventHandler? StopRequested;
    public event EventHandler? SkipRequested;

    public MacGlobalHotkeys()
    {
        // Kept as a field, not a local — a delegate passed to native code via
        // GetFunctionPointerForDelegate must stay alive for as long as native code can
        // call it, i.e. for the lifetime of this object.
        _handler = HandleHotKeyEvent;
        _isMacOs = OperatingSystem.IsMacOS();

        if (!_isMacOs)
        {
            return;
        }

        try
        {
            _target = GetApplicationEventTarget();
            var eventType = new EventTypeSpec { EventClass = EventClassKeyboard, EventKind = EventHotKeyPressed };

            if (InstallEventHandler(_target, Marshal.GetFunctionPointerForDelegate(_handler), 1,
                    new[] { eventType }, IntPtr.Zero, out _handlerRef) == 0)
            {
                _available = true;
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _available = false;
        }
    }

    public HotkeyApplyResult Apply(HotkeyCombo? startPause, HotkeyCombo? stop, HotkeyCombo? skip)
    {
        if (!_available)
        {
            return new HotkeyApplyResult(false, false, false);
        }

        lock (_applyLock)
        {
            try
            {
                Unregister(ref _startPauseRef);
                Unregister(ref _stopRef);
                Unregister(ref _skipRef);

                _startPauseRef = Register(StartPauseId, startPause);
                _stopRef = Register(StopId, stop);
                _skipRef = Register(SkipId, skip);

                return new HotkeyApplyResult(
                    startPause is null || _startPauseRef != IntPtr.Zero,
                    stop is null || _stopRef != IntPtr.Zero,
                    skip is null || _skipRef != IntPtr.Zero);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                return new HotkeyApplyResult(false, false, false);
            }
        }
    }

    /// <summary>Null combo, or a key outside <see cref="CarbonKeyCodes"/>, registers nothing.</summary>
    private IntPtr Register(uint id, HotkeyCombo? combo)
    {
        if (combo is not { } value || !CarbonKeyCodes.TryGetValue(value.Key, out var keyCode))
        {
            return IntPtr.Zero;
        }

        var hotKeyId = new EventHotKeyID { Signature = HotKeySignature, Id = id };
        return RegisterEventHotKey(keyCode, ToCarbonModifiers(value.Modifiers), hotKeyId, _target, 0, out var outRef) == 0
            ? outRef
            : IntPtr.Zero;
    }

    private static void Unregister(ref IntPtr hotKeyRef)
    {
        if (hotKeyRef == IntPtr.Zero)
        {
            return;
        }

        UnregisterEventHotKey(hotKeyRef);
        hotKeyRef = IntPtr.Zero;
    }

    private static uint ToCarbonModifiers(HotkeyModifiers modifiers)
    {
        uint result = 0;

        if (modifiers.HasFlag(HotkeyModifiers.Meta))
        {
            result |= CmdKey;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            result |= OptionKey;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Control))
        {
            result |= ControlKey;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            result |= ShiftKey;
        }

        return result;
    }

    /// <summary>
    /// The Carbon event callback. Returning non-zero would mean "handled, don't propagate
    /// further" — always returns noErr (0) instead, since nothing else in the app is
    /// listening for these events and there's no reason to claim otherwise.
    /// </summary>
    private int HandleHotKeyEvent(IntPtr nextHandler, IntPtr theEvent, IntPtr userData)
    {
        try
        {
            if (GetEventParameter(theEvent, ParamDirectObject, TypeEventHotKeyId, IntPtr.Zero,
                    (uint)Marshal.SizeOf<EventHotKeyID>(), IntPtr.Zero, out var hotKeyId) != 0)
            {
                return 0;
            }

            switch (hotKeyId.Id)
            {
                case StartPauseId:
                    StartPauseRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case StopId:
                    StopRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case SkipId:
                    SkipRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // Best effort — see the class remarks on why this never throws outward.
        }

        return 0;
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
            lock (_applyLock)
            {
                Unregister(ref _startPauseRef);
                Unregister(ref _stopRef);
                Unregister(ref _skipRef);
            }

            if (_handlerRef != IntPtr.Zero)
            {
                RemoveEventHandler(_handlerRef);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // The process is exiting either way; nothing left to hand this off to.
        }
    }

    private static uint FourCC(string code) =>
        (uint)((code[0] << 24) | (code[1] << 16) | (code[2] << 8) | code[3]);

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTypeSpec
    {
        public uint EventClass;
        public uint EventKind;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHotKeyID
    {
        public uint Signature;
        public uint Id;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EventHandlerProc(IntPtr nextHandler, IntPtr theEvent, IntPtr userData);

    [DllImport(CarbonLib)]
    private static extern IntPtr GetApplicationEventTarget();

    [DllImport(CarbonLib)]
    private static extern int InstallEventHandler(
        IntPtr target, IntPtr handler, uint numTypes, EventTypeSpec[] list, IntPtr userData, out IntPtr outRef);

    [DllImport(CarbonLib)]
    private static extern int RemoveEventHandler(IntPtr handlerRef);

    [DllImport(CarbonLib)]
    private static extern int RegisterEventHotKey(
        uint keyCode, uint modifiers, EventHotKeyID hotKeyId, IntPtr target, uint options, out IntPtr outRef);

    [DllImport(CarbonLib)]
    private static extern int UnregisterEventHotKey(IntPtr hotKeyRef);

    [DllImport(CarbonLib)]
    private static extern int GetEventParameter(
        IntPtr theEvent, uint name, uint desiredType, IntPtr actualType, uint bufferSize,
        IntPtr actualSize, out EventHotKeyID data);
}
