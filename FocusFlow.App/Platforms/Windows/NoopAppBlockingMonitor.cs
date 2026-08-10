using System;
using System.Collections.Generic;
using FocusFlow.Application.Interfaces;
using FocusFlow.Domain.Models;

namespace FocusFlow.App.Platforms.Windows;

/// <summary>
/// App blocking is macOS-only for this pass — see MacAppBlockingMonitor. Nothing to do
/// here; IsSupported is false so AppBlockingService never starts watching.
/// </summary>
public sealed class NoopAppBlockingMonitor : IAppBlockingMonitor
{
    public bool IsSupported => false;

    public event EventHandler<string>? AppActivated
    {
        add { }
        remove { }
    }

    public IReadOnlyList<AppInfo> GetInstalledApplications() => [];

    public void RequestAccessibilityAccess()
    {
    }

    public string? GetFrontmostBundleId() => null;

    public AppInfo? GetFrontmostApp() => null;

    public void StartWatching()
    {
    }

    public void StopWatching()
    {
    }

    public void Intervene(string bundleId)
    {
    }

    public void Dispose()
    {
    }
}
