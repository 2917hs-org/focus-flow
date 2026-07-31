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
    private const string Label = "com.focusflow.app";

    public bool IsSupported => OperatingSystem.IsMacOS() && ExecutablePath() is not null;

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
            File.WriteAllText(path, BuildPlist(ExecutablePath()!));
            RunLaunchctl("load", path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// The apphost next to the managed assembly. Environment.ProcessPath points at the
    /// dotnet host during `dotnet run`, which would register the wrong thing.
    /// </summary>
    private static string? ExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        return Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? null
            : path;
    }

    private static string BuildPlist(string executablePath) =>
        $"""
         <?xml version="1.0" encoding="UTF-8"?>
         <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
         <plist version="1.0">
         <dict>
             <key>Label</key>
             <string>{Label}</string>
             <key>ProgramArguments</key>
             <array>
                 <string>{SecurityElement.Escape(executablePath)}</string>
             </array>
             <key>RunAtLoad</key>
             <true/>
         </dict>
         </plist>

         """;

    private static void RunLaunchctl(string verb, string plistPath)
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
        catch (Exception)
        {
            // The plist alone is enough from the next login onwards.
        }
    }
}
