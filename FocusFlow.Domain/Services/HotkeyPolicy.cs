using FocusFlow.Domain.Models;

namespace FocusFlow.Domain.Services;

/// <summary>
/// Whether two hotkey bindings would fight over the same physical key combination.
/// </summary>
/// <remarks>
/// Pure function over already-known state, same shape as <see cref="AppBlockPolicy"/>: no
/// native code needed to decide this, only used to stop FocusFlow's own three bindings
/// from colliding with each other. Collisions with other apps or OS shortcuts can only be
/// discovered by the actual OS registration call failing — see IGlobalHotkeys.Apply.
/// </remarks>
public static class HotkeyPolicy
{
    public static bool Conflicts(HotkeyBinding a, HotkeyBinding b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        return a.Enabled && b.Enabled && a.Modifiers == b.Modifiers
            && string.Equals(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
    }
}
