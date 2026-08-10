using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using FocusFlow.Application.Interfaces;
using FocusFlow.Domain.Models;

namespace FocusFlow.App.Platforms.Windows;

/// <summary>
/// User-configurable global keyboard shortcuts (see README for the defaults, and Settings
/// for changing them) via Win32 <c>RegisterHotKey</c>, delivered through a hidden window.
/// </summary>
/// <remarks>
/// <para>
/// <c>RegisterHotKey</c> posts <c>WM_HOTKEY</c> to the message queue of whichever window
/// (or thread) it's registered against, so something has to own a Win32 message loop to
/// ever see it. Avalonia's own Win32 backend owns the main thread's loop and offers no hook
/// to intercept arbitrary window messages, so this runs its own tiny window and loop on a
/// dedicated background thread instead — self-contained, and it can never race or interfere
/// with Avalonia's own message pump.
/// </para>
/// <para>
/// The window is a real (if invisible) top-level window, not <c>HWND_MESSAGE</c>: hot keys
/// registered against a pure message-only window are delivered the same way, but a plain
/// invisible window keeps the Win32 plumbing here identical to any textbook hidden-window
/// sample, which matters more than the last bit of purity for a class nobody will look at
/// twice.
/// </para>
/// <para>
/// <c>RegisterHotKey</c>/<c>UnregisterHotKey</c> only work on the thread that owns the
/// target window ("This function fails if you try to associate a hot key with a window
/// created by another thread" — Win32 docs), so <see cref="Apply"/> can't call them
/// directly from whatever thread invokes it (the UI thread). Instead it stashes the
/// pending combinations, posts a custom message to the hidden window, and blocks on a
/// wait handle the message-loop thread signals once it has actually done the
/// (un)registration on its own thread — the same shape as the constructor's existing
/// startup handshake via <see cref="_ready"/>.
/// </para>
/// <para>
/// Only letters and digits are supported keys (see <see cref="VirtualKeyCodes"/>) —
/// Settings never offers anything else to capture.
/// </para>
/// <para>
/// Every native call is wrapped in the same defensive try/catch the macOS interop classes
/// use — a missing symbol or failed registration degrades to "no hotkeys," never a crash.
/// </para>
/// </remarks>
public sealed class WindowsGlobalHotkeys : IGlobalHotkeys
{
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_CLOSE = 0x0010;

    /// <summary>Custom message: "the pending combinations are ready, (re)register them now."</summary>
    private const uint WM_APPLY_HOTKEYS = 0x8000 + 1;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    // Stops the OS from repeating WM_HOTKEY while the combination is held down.
    private const uint MOD_NOREPEAT = 0x4000;

    private const int StartPauseId = 1;
    private const int StopId = 2;
    private const int SkipId = 3;

    private const string ClassName = "FocusFlowGlobalHotkeyWindow";

    /// <summary>
    /// Win32 virtual-key codes, keyed by the exact Avalonia Key enum member name Settings'
    /// capture control can produce. Letters and digits are ASCII-aligned in Win32, so this
    /// is just the character itself.
    /// </summary>
    private static readonly Dictionary<string, uint> VirtualKeyCodes = BuildVirtualKeyCodes();

    private static Dictionary<string, uint> BuildVirtualKeyCodes()
    {
        var codes = new Dictionary<string, uint>();

        for (var c = 'A'; c <= 'Z'; c++)
        {
            codes[c.ToString()] = c;
        }

        for (var d = 0; d <= 9; d++)
        {
            codes[$"D{d}"] = (uint)('0' + d);
        }

        return codes;
    }

    private readonly bool _isWindows;
    private readonly ManualResetEventSlim _ready = new(initialState: false);
    private readonly ManualResetEventSlim _applyReady = new(initialState: false);
    private readonly object _applyLock = new();
    private Thread? _thread;

    // Kept as a field, not a local — a delegate passed to native code via
    // GetFunctionPointerForDelegate must stay alive for as long as native code can call it.
    private WndProc? _wndProc;

    private IntPtr _hwnd;
    private bool _available;
    private bool _disposed;

    // Written by Apply() (any thread) before posting, read only by the message-loop
    // thread while handling WM_APPLY_HOTKEYS — safe because _applyLock serializes callers
    // and the wait handle establishes happens-before with the read.
    private HotkeyCombo? _pendingStartPause;
    private HotkeyCombo? _pendingStop;
    private HotkeyCombo? _pendingSkip;
    private HotkeyApplyResult _applyResult = new(false, false, false);

    public event EventHandler? StartPauseRequested;
    public event EventHandler? StopRequested;
    public event EventHandler? SkipRequested;

    public WindowsGlobalHotkeys()
    {
        _isWindows = OperatingSystem.IsWindows();

        if (!_isWindows)
        {
            return;
        }

        _thread = new Thread(RunMessageLoop) { IsBackground = true, Name = "FocusFlow.GlobalHotkeys" };
        _thread.Start();

        // Window creation happens on the message-loop thread; block briefly so a caller
        // that immediately calls Apply() doesn't race construction.
        _ready.Wait(TimeSpan.FromSeconds(2));
    }

    public HotkeyApplyResult Apply(HotkeyCombo? startPause, HotkeyCombo? stop, HotkeyCombo? skip)
    {
        if (!_isWindows)
        {
            return new HotkeyApplyResult(false, false, false);
        }

        lock (_applyLock)
        {
            if (_disposed || !_available)
            {
                return new HotkeyApplyResult(false, false, false);
            }

            _pendingStartPause = startPause;
            _pendingStop = stop;
            _pendingSkip = skip;
            _applyReady.Reset();

            if (!PostMessageW(_hwnd, WM_APPLY_HOTKEYS, IntPtr.Zero, IntPtr.Zero))
            {
                // The window is already gone (e.g. Dispose raced us) — nothing to apply.
                return new HotkeyApplyResult(false, false, false);
            }

            return _applyReady.Wait(TimeSpan.FromSeconds(2))
                ? _applyResult
                : new HotkeyApplyResult(false, false, false);
        }
    }

    private void RunMessageLoop()
    {
        try
        {
            _wndProc = HandleWindowMessage;

            var wndClass = new WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = GetModuleHandleW(null),
                lpszClassName = ClassName
            };

            if (RegisterClassW(ref wndClass) != 0)
            {
                _hwnd = CreateWindowExW(0, ClassName, ClassName, 0, 0, 0, 0, 0,
                    IntPtr.Zero, IntPtr.Zero, wndClass.hInstance, IntPtr.Zero);

                _available = _hwnd != IntPtr.Zero;
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _available = false;
        }
        finally
        {
            _ready.Set();
        }

        if (!_available)
        {
            return;
        }

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        UnregisterClassW(ClassName, GetModuleHandleW(null));
    }

    private IntPtr HandleWindowMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_HOTKEY:
                switch (wParam.ToInt32())
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

                return IntPtr.Zero;

            case WM_APPLY_HOTKEYS:
                ApplyPending(hWnd);
                return IntPtr.Zero;

            case WM_DESTROY:
                UnregisterHotKey(hWnd, StartPauseId);
                UnregisterHotKey(hWnd, StopId);
                UnregisterHotKey(hWnd, SkipId);
                PostQuitMessage(0);
                return IntPtr.Zero;

            default:
                return DefWindowProcW(hWnd, msg, wParam, lParam);
        }
    }

    /// <summary>Runs on the message-loop thread — the only thread allowed to call RegisterHotKey for this window.</summary>
    private void ApplyPending(IntPtr hWnd)
    {
        UnregisterHotKey(hWnd, StartPauseId);
        UnregisterHotKey(hWnd, StopId);
        UnregisterHotKey(hWnd, SkipId);

        var startPauseOk = Register(hWnd, StartPauseId, _pendingStartPause);
        var stopOk = Register(hWnd, StopId, _pendingStop);
        var skipOk = Register(hWnd, SkipId, _pendingSkip);

        _applyResult = new HotkeyApplyResult(startPauseOk, stopOk, skipOk);
        _applyReady.Set();
    }

    /// <summary>Null combo, or a key outside <see cref="VirtualKeyCodes"/>, registers nothing but still "succeeds" (nothing to fail).</summary>
    private static bool Register(IntPtr hWnd, int id, HotkeyCombo? combo)
    {
        if (combo is not { } value)
        {
            return true;
        }

        return VirtualKeyCodes.TryGetValue(value.Key, out var vk)
            && RegisterHotKey(hWnd, id, ToWin32Modifiers(value.Modifiers), vk);
    }

    private static uint ToWin32Modifiers(HotkeyModifiers modifiers)
    {
        uint result = MOD_NOREPEAT;

        if (modifiers.HasFlag(HotkeyModifiers.Control))
        {
            result |= MOD_CONTROL;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            result |= MOD_ALT;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            result |= MOD_SHIFT;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Meta))
        {
            result |= MOD_WIN;
        }

        return result;
    }

    public void Dispose()
    {
        lock (_applyLock)
        {
            if (!_isWindows || _disposed)
            {
                return;
            }

            _disposed = true;

            // WM_CLOSE's default handling (DefWindowProc) calls DestroyWindow, which sends
            // WM_DESTROY — handled above to unregister the hot keys and stop the loop.
            if (_available && _hwnd != IntPtr.Zero)
            {
                PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
        }

        _thread?.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
        _applyReady.Dispose();
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", EntryPoint = "RegisterClassW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
