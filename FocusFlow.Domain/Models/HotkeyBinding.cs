namespace FocusFlow.Domain.Models;

/// <summary>
/// One user-configurable global hotkey slot.
/// </summary>
/// <remarks>
/// A record, not a class — "never mutated in place, always replaced wholesale" is a
/// compiler-enforced invariant here rather than a convention, which is what lets
/// <see cref="TimerConfig.Clone"/> get away with a plain reference copy for these fields
/// instead of a real member-wise clone.
/// <para>
/// <c>new HotkeyBinding()</c> — the default for an upgrading user with no stored value yet
/// — means "enabled, use this platform's built-in default combination," so nothing changes
/// for existing users until they customize one. Disabling a binding doesn't clear
/// <see cref="Modifiers"/>/<see cref="Key"/>, so re-enabling it remembers the last custom
/// combination rather than resetting to the default.
/// </para>
/// </remarks>
/// <param name="Enabled">Whether this action has an active hotkey at all.</param>
/// <param name="Modifiers">Ignored when <see cref="Key"/> is empty.</param>
/// <param name="Key">
/// An Avalonia Key enum member name (e.g. "P", "D5"). Empty means "use this platform's
/// built-in default combination" rather than a specific custom one.
/// </param>
public sealed record HotkeyBinding(bool Enabled = true, HotkeyModifiers Modifiers = HotkeyModifiers.None, string Key = "");
