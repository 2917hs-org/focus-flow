namespace FocusFlow.Domain.Models;

/// <summary>
/// Modifier keys for a <see cref="HotkeyBinding"/>. Values deliberately match
/// Avalonia.Input.KeyModifiers numerically, but the App layer still converts bit-by-bit
/// rather than casting, so a future Avalonia change can't silently break this — Domain has
/// no dependency on Avalonia at all.
/// </summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Meta = 8
}
