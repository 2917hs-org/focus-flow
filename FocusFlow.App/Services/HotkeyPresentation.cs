using System;
using System.Collections.Generic;
using System.Text;
using Avalonia.Input;
using FocusFlow.Application.Interfaces;
using FocusFlow.Domain.Models;

namespace FocusFlow.App.Services;

/// <summary>
/// Turns a resolved <see cref="HotkeyCombo"/> into something a human or Avalonia can use:
/// display text for Settings/tooltips, or a <see cref="KeyGesture"/> for the tray menu.
/// </summary>
public static class HotkeyPresentation
{
    /// <summary>
    /// "⌃⌥⌘P" on macOS, "Ctrl+Alt+Shift+P" on Windows, "Off" when there's no hotkey for
    /// this action.
    /// </summary>
    public static string Format(HotkeyCombo? combo)
    {
        if (combo is not { } value)
        {
            return "Off";
        }

        var key = DisplayKeyName(value.Key);

        if (OperatingSystem.IsMacOS())
        {
            var sb = new StringBuilder();
            if (value.Modifiers.HasFlag(HotkeyModifiers.Control))
            {
                sb.Append('⌃');
            }

            if (value.Modifiers.HasFlag(HotkeyModifiers.Alt))
            {
                sb.Append('⌥');
            }

            if (value.Modifiers.HasFlag(HotkeyModifiers.Shift))
            {
                sb.Append('⇧');
            }

            if (value.Modifiers.HasFlag(HotkeyModifiers.Meta))
            {
                sb.Append('⌘');
            }

            sb.Append(key);
            return sb.ToString();
        }

        var parts = new List<string>();
        if (value.Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (value.Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (value.Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (value.Modifiers.HasFlag(HotkeyModifiers.Meta))
        {
            parts.Add("Win");
        }

        parts.Add(key);
        return string.Join("+", parts);
    }

    /// <summary>Null if the combo is unset or its Key isn't a recognised Avalonia Key name.</summary>
    public static KeyGesture? ToKeyGesture(HotkeyCombo? combo)
    {
        if (combo is not { } value || !Enum.TryParse<Key>(value.Key, out var key))
        {
            return null;
        }

        var modifiers = KeyModifiers.None;
        if (value.Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            modifiers |= KeyModifiers.Control;
        }

        if (value.Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            modifiers |= KeyModifiers.Alt;
        }

        if (value.Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            modifiers |= KeyModifiers.Shift;
        }

        if (value.Modifiers.HasFlag(HotkeyModifiers.Meta))
        {
            modifiers |= KeyModifiers.Meta;
        }

        return new KeyGesture(key, modifiers);
    }

    /// <summary>
    /// "D5" (Avalonia's name for the digit key) displays as "5"; everything else, including
    /// the single-character letter "D" itself, displays as-is.
    /// </summary>
    private static string DisplayKeyName(string key) =>
        key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1]) ? key[1..] : key;
}
