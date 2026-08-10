using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Threading;
using FocusFlow.Application.Interfaces;
using FocusFlow.Domain.Models;

namespace FocusFlow.App.Platforms.MacOS;

/// <summary>
/// App blocking on macOS: polls the frontmost application and, while watching, hides any
/// match on the blocked list and brings FocusFlow forward.
/// </summary>
/// <remarks>
/// <para>
/// Polling <c>NSWorkspace.frontmostApplication</c> rather than observing
/// <c>NSWorkspace.didActivateApplicationNotification</c>: true notification-based
/// observation needs an Objective-C block callback (constructing the block ABI struct, a
/// native function pointer, <c>_Block_copy</c>) that nothing else in this codebase uses —
/// every existing interop call here (<see cref="NativeMenuBarCountdown"/>,
/// <see cref="MacPointerLocator"/>) is a synchronous objc_msgSend, never a callback. A
/// ~500ms poll reuses that same proven technique with no new interop primitive, at a
/// latency nobody will notice for "hide the app someone just switched to."
/// </para>
/// <para>
/// Raw objc_msgSend P/Invoke, not a binding library — same rationale as
/// <see cref="NativeMenuBarCountdown"/>. Every native call is wrapped in the same
/// defensive try/catch: a missing symbol degrades to "blocking does nothing," never a
/// crash.
/// </para>
/// <para>
/// Ticks are posted to the UI thread before touching AppKit — NSWorkspace reads are
/// generally thread-safe, but <c>hide</c> and <c>activateWithOptions:</c> are UI-affecting
/// calls Apple documents as expecting the main thread, and the poll timer itself runs on a
/// thread-pool thread.
/// </para>
/// <para>
/// The constructor calls <c>NSApplicationLoad()</c> before anything else touches
/// NSWorkspace/NSRunningApplication. Verified the hard way: a throwaway console harness
/// against this exact class returned null/zero from every AppKit call —
/// <c>objc_getClass("NSWorkspace")</c> itself came back 0 — because AppKit is not linked
/// into a process until something forces it to load, and a bare console process never
/// does. Avalonia's own windowing almost certainly loads it first in the real app, but nothing
/// here should depend on being constructed after that happens to be true — NSApplicationLoad
/// is the documented, idempotent way to guarantee it regardless of construction order.
/// </para>
/// </remarks>
public sealed class MacAppBlockingMonitor : IAppBlockingMonitor
{
    private const string ObjCLib = "/usr/lib/libobjc.dylib";

    private const string ApplicationServicesLib =
        "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

    private const string AppKitLib = "/System/Library/Frameworks/AppKit.framework/AppKit";

    /// <summary>
    /// Worst-case delay between switching to a blocked app and it actually getting hidden.
    /// Tightened from an initial 500ms after review flagged it as a perceptible lag — each
    /// tick is a couple of cheap objc_msgSend calls, not worth trading responsiveness for
    /// battery savings nobody would notice at this frequency.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>NSApplicationActivateIgnoringOtherApps.</summary>
    private static readonly IntPtr ActivateIgnoringOtherApps = (IntPtr)2;

    private readonly bool _isMacOs;
    private readonly IAppLogger? _logger;

    private Timer? _pollTimer;
    private string? _lastFrontmostBundleId;
    private bool _disposed;

    public MacAppBlockingMonitor(IAppLogger? logger = null)
    {
        _logger = logger;
        _isMacOs = OperatingSystem.IsMacOS();

        if (!_isMacOs)
        {
            return;
        }

        try
        {
            NSApplicationLoad();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // Best effort — see the class remarks on why this never throws outward. Every
            // AppKit call below degrades to returning nothing rather than working, same as
            // any other missing symbol.
        }
    }

    public event EventHandler<string>? AppActivated;

    public bool IsSupported => _isMacOs && IsProcessTrusted();

    /// <summary>
    /// Every app bundle Spotlight knows about, not just running ones — asking "what apps
    /// are running right now" would leave no way to pre-block something before its first
    /// launch of the day. Shells out to mdfind/mdls rather than reading Info.plist
    /// ourselves: Spotlight's index already has bundle id and display name for every
    /// indexed .app, and this codebase already shells out (osascript, launchctl) rather
    /// than binding a whole plist parser for things a CLI already does in one line.
    /// </summary>
    /// <remarks>
    /// One mdls process for every path in a chunk, not one per app: a first pass spawned
    /// mdls per app and took ~30s across the ~530 apps a typical Mac has installed — long
    /// enough to look hung even off the UI thread. mdls run over many paths at once prints
    /// each app's attributes back to back with no separator, but always in the same order
    /// the paths were given, which is enough to match lines back to paths by position. That
    /// dropped the same 530-app scan to well under a second.
    /// </remarks>
    public IReadOnlyList<AppInfo> GetInstalledApplications()
    {
        if (!_isMacOs)
        {
            return [];
        }

        try
        {
            var ownBundleId = ReadOwnBundleId();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<AppInfo>();

            foreach (var app in ReadBundleMetadata(FindInstalledAppBundlePaths()))
            {
                if (string.Equals(app.BundleId, ownBundleId, StringComparison.OrdinalIgnoreCase)
                    || !seen.Add(app.BundleId))
                {
                    continue;
                }

                result.Add(app);
            }

            return result;
        }
        catch (Exception)
        {
            // Best effort — see the class remarks on why this never throws outward. Broad
            // rather than the usual DllNotFound/EntryPointNotFound pair: this path shells
            // out to external processes, which fail in more ways than a missing symbol.
            return [];
        }
    }

    /// <summary>
    /// Every top-level .app bundle in the standard app folders, skipping helper bundles
    /// nested inside another .app.
    /// </summary>
    /// <remarks>
    /// Scoped to /Applications, /System/Applications(/Utilities) and ~/Applications rather
    /// than a system-wide mdfind: an unscoped search also surfaces things like Parallels'
    /// virtualised Windows Start Menu, which lives under
    /// "~/Applications (Parallels)/{guid} Applications.localized/" — a different bundle id
    /// per virtual app, but often the same display name as the real macOS app (three
    /// "Google Chrome" entries for one Chrome install), which reads as duplicates even
    /// though nothing is actually wrong. None of that lives in the folders a person would
    /// actually look in to find "my apps," so scoping there is a fix, not just a filter.
    /// </remarks>
    private static List<string> FindInstalledAppBundlePaths()
    {
        var result = new List<string>();

        foreach (var directory in AppDirectories())
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            result.AddRange(FindAppBundles(directory));
        }

        return result.Distinct(StringComparer.Ordinal).Where(IsTopLevelBundle).ToList();
    }

    private static IEnumerable<string> AppDirectories()
    {
        yield return "/Applications";
        yield return "/System/Applications";
        yield return "/System/Applications/Utilities";
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications");
    }

    private static IEnumerable<string> FindAppBundles(string directory)
    {
        var startInfo = new ProcessStartInfo("mdfind")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-onlyin");
        startInfo.ArgumentList.Add(directory);
        startInfo.ArgumentList.Add("kMDItemContentType == 'com.apple.application-bundle'");

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return [];
        }

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(10_000);

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool IsTopLevelBundle(string appPath)
    {
        var suffixIndex = appPath.LastIndexOf(".app", StringComparison.Ordinal);
        return suffixIndex > 0 && !appPath[..suffixIndex].Contains(".app/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Chunked well under a typical ARG_MAX so an unusually large /Applications doesn't
    /// blow the command-line length limit; ~530 apps in one chunk already ran in well
    /// under a second, so this is a safety margin, not a performance measure.
    /// </summary>
    private static IEnumerable<AppInfo> ReadBundleMetadata(IReadOnlyList<string> appPaths)
    {
        const int chunkSize = 300;

        for (var offset = 0; offset < appPaths.Count; offset += chunkSize)
        {
            var chunk = appPaths.Skip(offset).Take(chunkSize).ToList();
            foreach (var app in ReadBundleMetadataChunk(chunk))
            {
                yield return app;
            }
        }
    }

    private static IEnumerable<AppInfo> ReadBundleMetadataChunk(IReadOnlyList<string> appPaths)
    {
        if (appPaths.Count == 0)
        {
            yield break;
        }

        var startInfo = new ProcessStartInfo("mdls")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-name");
        startInfo.ArgumentList.Add("kMDItemCFBundleIdentifier");
        startInfo.ArgumentList.Add("-name");
        startInfo.ArgumentList.Add("kMDItemDisplayName");
        startInfo.ArgumentList.Add("-nullMarker");
        startInfo.ArgumentList.Add("");
        foreach (var path in appPaths)
        {
            startInfo.ArgumentList.Add(path);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            yield break;
        }

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(30_000);

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < appPaths.Count && i * 2 + 1 < lines.Length; i++)
        {
            var bundleId = ParseMdlsValue(lines[i * 2]);
            if (string.IsNullOrEmpty(bundleId))
            {
                continue;
            }

            var name = ParseMdlsValue(lines[i * 2 + 1]) ?? Path.GetFileNameWithoutExtension(appPaths[i]);
            if (name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^4];
            }

            yield return new AppInfo(bundleId, name);
        }
    }

    /// <summary>Parses one "key = value" line from mdls -name output; value is quoted, or empty via -nullMarker.</summary>
    private static string? ParseMdlsValue(string line)
    {
        var separator = line.IndexOf('=');
        if (separator < 0)
        {
            return null;
        }

        var value = line[(separator + 1)..].Trim().Trim('"');
        return value.Length == 0 ? null : value;
    }

    public string? GetFrontmostBundleId()
    {
        if (!_isMacOs)
        {
            return null;
        }

        try
        {
            return ReadFrontmostBundleId();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// A separate, slightly heavier read from GetFrontmostBundleId — that one is also
    /// called every poll tick, so it only ever reads bundleIdentifier; this reads
    /// localizedName too, worth the extra objc_msgSend call only for on-demand uses like
    /// the tray menu's "Block Frontmost App" quick-add.
    /// </summary>
    public AppInfo? GetFrontmostApp()
    {
        if (!_isMacOs)
        {
            return null;
        }

        try
        {
            var frontmost = SendIntPtr(SharedWorkspace(), Sel("frontmostApplication"));
            if (frontmost == IntPtr.Zero)
            {
                return null;
            }

            var bundleId = ReadNSString(SendIntPtr(frontmost, Sel("bundleIdentifier")));
            if (string.IsNullOrEmpty(bundleId))
            {
                return null;
            }

            var name = ReadNSString(SendIntPtr(frontmost, Sel("localizedName")));
            return new AppInfo(bundleId, string.IsNullOrEmpty(name) ? bundleId : name);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    public void RequestAccessibilityAccess()
    {
        if (!_isMacOs)
        {
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo("open")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(
                "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility");

            using var process = Process.Start(startInfo);
        }
        catch (Exception e)
        {
            _logger?.Warn($"Opening Accessibility settings failed: {e.Message}");
        }
    }

    public void StartWatching()
    {
        if (!_isMacOs || _pollTimer is not null)
        {
            return;
        }

        _pollTimer = new Timer(_ => Dispatcher.UIThread.Post(Tick), null, PollInterval, PollInterval);
    }

    public void StopWatching()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
        _lastFrontmostBundleId = null;
    }

    public void Intervene(string bundleId)
    {
        ArgumentNullException.ThrowIfNull(bundleId);

        if (!_isMacOs)
        {
            return;
        }

        try
        {
            var runningAppClass = objc_getClass("NSRunningApplication");
            var nsBundleId = SendIntPtrFromUtf8(objc_getClass("NSString"), Sel("stringWithUTF8String:"), bundleId);
            var matches = SendIntPtrFromIntPtr(
                runningAppClass, Sel("runningApplicationsWithBundleIdentifier:"), nsBundleId);

            if (SendIntPtr(matches, Sel("count")).ToInt64() > 0)
            {
                var target = SendIntPtrFromIntPtr(matches, Sel("objectAtIndex:"), IntPtr.Zero);
                SendBool(target, Sel("hide"));
            }

            var current = SendIntPtr(runningAppClass, Sel("currentApplication"));
            SendBoolFromIntPtr(current, Sel("activateWithOptions:"), ActivateIgnoringOtherApps);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // Best effort — see the class remarks on why this never throws outward.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopWatching();
    }

    private void Tick()
    {
        try
        {
            var bundleId = ReadFrontmostBundleId();

            if (string.Equals(bundleId, _lastFrontmostBundleId, StringComparison.Ordinal))
            {
                return;
            }

            _lastFrontmostBundleId = bundleId;

            if (!string.IsNullOrEmpty(bundleId))
            {
                AppActivated?.Invoke(this, bundleId);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // Best effort — see the class remarks on why this never throws outward.
        }
    }

    /// <summary>Shared by the poll tick and the on-demand GetFrontmostBundleId — caller owns the try/catch.</summary>
    private static string? ReadFrontmostBundleId()
    {
        var frontmost = SendIntPtr(SharedWorkspace(), Sel("frontmostApplication"));
        return frontmost == IntPtr.Zero ? null : ReadNSString(SendIntPtr(frontmost, Sel("bundleIdentifier")));
    }

    private static bool IsProcessTrusted()
    {
        try
        {
            return AXIsProcessTrusted();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static IntPtr SharedWorkspace() => SendIntPtr(objc_getClass("NSWorkspace"), Sel("sharedWorkspace"));

    private static string? ReadOwnBundleId()
    {
        var bundle = SendIntPtr(objc_getClass("NSBundle"), Sel("mainBundle"));
        return ReadNSString(SendIntPtr(bundle, Sel("bundleIdentifier")));
    }

    private static string? ReadNSString(IntPtr nsString)
    {
        if (nsString == IntPtr.Zero)
        {
            return null;
        }

        var utf8 = SendIntPtr(nsString, Sel("UTF8String"));
        return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
    }

    private static IntPtr Sel(string name) => sel_registerName(name);

    [DllImport(ApplicationServicesLib)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AXIsProcessTrusted();

    // objc_msgSend has no fixed signature — see NativeMenuBarCountdown's remarks for why
    // each distinct call shape below needs its own DllImport entry point.
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendIntPtr(IntPtr receiver, IntPtr selector);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendIntPtrFromIntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendIntPtrFromUtf8(
        IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg1);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendBool(IntPtr receiver, IntPtr selector);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendBoolFromIntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1);

    [DllImport(ObjCLib)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjCLib)]
    private static extern IntPtr sel_registerName(string name);

    /// <summary>
    /// Forces AppKit to fully load in a process that doesn't otherwise run a full
    /// application — the documented mechanism for exactly this, per Apple's own docs.
    /// </summary>
    [DllImport(AppKitLib)]
    private static extern void NSApplicationLoad();
}
