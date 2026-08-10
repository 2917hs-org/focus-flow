namespace FocusFlow.Domain.Models;

/// <summary>A user-facing app installed on the machine, as offered to the blocked-apps picker.</summary>
public sealed record AppInfo(string BundleId, string DisplayName);
