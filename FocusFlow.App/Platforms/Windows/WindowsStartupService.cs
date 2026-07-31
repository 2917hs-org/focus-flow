using System;
using System.IO;
using System.Security;
using FocusFlow.Application.Interfaces;
using Microsoft.Win32;

namespace FocusFlow.App.Platforms.Windows;

/// <summary>
/// FR-012 on Windows, via the per-user Run key.
/// </summary>
/// <remarks>
/// HKEY_CURRENT_USER rather than HKEY_LOCAL_MACHINE: the per-user key needs no elevation
/// and only affects the person who ticked the box.
/// </remarks>
public sealed class WindowsStartupService : IStartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FocusFlow";

    public bool IsSupported => OperatingSystem.IsWindows() && ExecutablePath() is not null;

    public bool IsEnabled()
    {
        if (!IsSupported)
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public bool SetEnabled(bool enabled)
    {
        if (!IsSupported)
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                // Quoted: the path routinely contains spaces (Program Files, user names).
                key.SetValue(ValueName, $"\"{ExecutablePath()}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
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
}
