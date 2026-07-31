using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using FocusFlow.Application.Interfaces;
using Microsoft.Win32;

namespace FocusFlow.App.Platforms.Windows;

/// <summary>
/// FR-012 on Windows, via the per-user Run key.
/// </summary>
/// <remarks>
/// <para>
/// HKEY_CURRENT_USER rather than HKEY_LOCAL_MACHINE: the per-user key needs no elevation
/// and only affects the person who ticked the box.
/// </para>
/// <para>
/// The Run key only works for an unpackaged build. Under MSIX the registry is virtualised,
/// so the value would land in the package's private hive and Windows would never act on
/// it — the checkbox would claim success and nothing would happen at login. A packaged
/// build declares a windows.startupTask in its manifest instead, which the user controls
/// from Settings, so this reports itself unsupported rather than lying.
/// </para>
/// </remarks>
public sealed class WindowsStartupService : IStartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FocusFlow";

    public bool IsSupported =>
        OperatingSystem.IsWindows() && !IsPackaged() && ExecutablePath() is not null;

    /// <summary>
    /// True when running from an MSIX package, where registry writes are virtualised.
    /// </summary>
    private static bool IsPackaged()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var length = 0;
            // APPMODEL_ERROR_NO_PACKAGE (15700) means unpackaged; anything else means the
            // process has package identity.
            return GetCurrentPackageFullName(ref length, null) != 15700;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // Pre-Windows 8 kernel32 without the appmodel APIs: treat as unpackaged.
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);

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
