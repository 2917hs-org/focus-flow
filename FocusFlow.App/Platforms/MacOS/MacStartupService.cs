using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using FocusFlow.Application.Interfaces;

namespace FocusFlow.App.Platforms.MacOS;

/// <summary>
/// FR-012 on macOS, via a per-user LaunchAgent.
/// </summary>
/// <remarks>
/// Writes to ~/Library/LaunchAgents, which is the user's own directory — no admin rights
/// and no system-wide change. The agent is loaded/unloaded with launchctl so the setting
/// takes effect without a logout.
/// </remarks>
public sealed class MacStartupService : IStartupService
{
    /// <summary>
    /// Matches CFBundleIdentifier in build/macos/Info.plist. launchd labels conventionally
    /// mirror the bundle id, and a mismatch makes the agent hard to find alongside the app.
    /// </summary>
    private const string Label = "com.hasansiddiqui.focusflow";

    private readonly IAppLogger? _logger;

    public MacStartupService(IAppLogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Only offered from a real .app bundle — see <see cref="BundlePath"/> for why the raw
    /// executable is not good enough.
    /// </summary>
    public bool IsSupported => OperatingSystem.IsMacOS() && BundlePath() is not null;

    private static string PlistPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library",
        "LaunchAgents",
        Label + ".plist");

    public bool IsEnabled() => IsSupported && File.Exists(PlistPath());

    public bool SetEnabled(bool enabled)
    {
        if (!IsSupported)
        {
            return false;
        }

        var path = PlistPath();

        try
        {
            if (!enabled)
            {
                if (File.Exists(path))
                {
                    RunLaunchctl("unload", path);
                    File.Delete(path);
                }

                return true;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, BuildPlist(BundlePath()!));
            RunLaunchctl("load", path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            // The caller already surfaces this as "Couldn't update the login item" —
            // the detail below is what explains why, next time someone has to dig in.
            _logger?.Warn($"Updating the login item failed: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Walks up from the running executable to the enclosing .app bundle, or null when
    /// there isn't one (a `dotnet run` build, or a loose publish folder).
    /// </summary>
    /// <remarks>
    /// The agent must launch the bundle, not the executable inside it. Starting
    /// Contents/MacOS/FocusFlow directly bypasses LaunchServices, and the app then comes up
    /// with a Dock tile despite LSUIElement — verified while packaging: the same build
    /// shows a Dock icon when the binary is run directly and none when opened as a bundle.
    /// Launch-at-login would otherwise contradict the whole menu-bar design.
    /// </remarks>
    private static string? BundlePath()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            return null;
        }

        // .../FocusFlow.app/Contents/MacOS/FocusFlow
        var macOsDir = Path.GetDirectoryName(exe);
        var contents = Path.GetDirectoryName(macOsDir);
        var bundle = Path.GetDirectoryName(contents);

        var looksLikeBundle = bundle is not null
            && Path.GetFileName(macOsDir) == "MacOS"
            && Path.GetFileName(contents) == "Contents"
            && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase);

        return looksLikeBundle ? bundle : null;
    }

    private static string BuildPlist(string bundlePath) =>
        $"""
         <?xml version="1.0" encoding="UTF-8"?>
         <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
         <plist version="1.0">
         <dict>
             <key>Label</key>
             <string>{Label}</string>
             <key>ProgramArguments</key>
             <array>
                 <string>/usr/bin/open</string>
                 <string>-a</string>
                 <string>{SecurityElement.Escape(bundlePath)}</string>
             </array>
             <key>RunAtLoad</key>
             <true/>
         </dict>
         </plist>

         """;

    private void RunLaunchctl(string verb, string plistPath)
    {
        try
        {
            var startInfo = new ProcessStartInfo("launchctl")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(verb);
            startInfo.ArgumentList.Add(plistPath);

            using var process = Process.Start(startInfo);
            process?.WaitForExit(5000);
        }
        catch (Exception e)
        {
            // The plist alone is enough from the next login onwards.
            _logger?.Warn($"launchctl {verb} failed: {e.Message}");
        }
    }
}
