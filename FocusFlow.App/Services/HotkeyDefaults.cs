using System;
using FocusFlow.Application.Interfaces;
using FocusFlow.Domain.Models;

namespace FocusFlow.App.Services;

/// <summary>
/// The platform's built-in default hotkey combinations, and resolution of a
/// <see cref="HotkeyBinding"/> (which may mean "use the default" or "disabled") down to a
/// concrete <see cref="HotkeyCombo"/>.
/// </summary>
/// <remarks>
/// Reuses today's exact P/S/K keys and modifier sets, so an upgrading user with no stored
/// preference sees no behaviour change. Computed via a runtime <c>OperatingSystem</c>
/// check rather than conditional compilation because, unlike the native interop classes,
/// this file is shared and compiled on both TFMs — the same style App.axaml.cs already
/// uses for macOS-only styling.
/// </remarks>
public static class HotkeyDefaults
{
    public static readonly HotkeyCombo StartPause = Combo("P");
    public static readonly HotkeyCombo Stop = Combo("S");
    public static readonly HotkeyCombo Skip = Combo("K");

    private static HotkeyCombo Combo(string key) => new(
        OperatingSystem.IsMacOS()
            ? HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Meta
            : HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift,
        key);

    /// <summary>Null means "no hotkey for this action" — disabled, not merely unresolved.</summary>
    public static HotkeyCombo? Resolve(HotkeyBinding binding, HotkeyCombo fallback)
    {
        ArgumentNullException.ThrowIfNull(binding);

        if (!binding.Enabled)
        {
            return null;
        }

        return string.IsNullOrEmpty(binding.Key) ? fallback : new HotkeyCombo(binding.Modifiers, binding.Key);
    }
}
