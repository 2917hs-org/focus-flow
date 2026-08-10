using FocusFlow.Domain.Models;
using FocusFlow.Domain.Services;

namespace FocusFlow.Domain.Tests;

/// <summary>
/// Pure logic — no fakes needed, everything HotkeyPolicy needs is already on
/// <see cref="HotkeyBinding"/>.
/// </summary>
public class HotkeyPolicyTests
{
    private static HotkeyBinding Binding(bool enabled, HotkeyModifiers modifiers, string key) =>
        new(enabled, modifiers, key);

    [Fact]
    public void Conflicts_WhenBothEnabledWithSameModifiersAndKey_ReturnsTrue()
    {
        var a = Binding(true, HotkeyModifiers.Control | HotkeyModifiers.Alt, "P");
        var b = Binding(true, HotkeyModifiers.Control | HotkeyModifiers.Alt, "P");

        Assert.True(HotkeyPolicy.Conflicts(a, b));
    }

    [Fact]
    public void Conflicts_WhenKeysDiffer_ReturnsFalse()
    {
        var a = Binding(true, HotkeyModifiers.Control | HotkeyModifiers.Alt, "P");
        var b = Binding(true, HotkeyModifiers.Control | HotkeyModifiers.Alt, "S");

        Assert.False(HotkeyPolicy.Conflicts(a, b));
    }

    [Fact]
    public void Conflicts_WhenModifiersDiffer_ReturnsFalse()
    {
        var a = Binding(true, HotkeyModifiers.Control | HotkeyModifiers.Alt, "P");
        var b = Binding(true, HotkeyModifiers.Control | HotkeyModifiers.Shift, "P");

        Assert.False(HotkeyPolicy.Conflicts(a, b));
    }

    [Fact]
    public void Conflicts_WhenEitherIsDisabled_ReturnsFalse()
    {
        var a = Binding(false, HotkeyModifiers.Control | HotkeyModifiers.Alt, "P");
        var b = Binding(true, HotkeyModifiers.Control | HotkeyModifiers.Alt, "P");

        Assert.False(HotkeyPolicy.Conflicts(a, b));
        Assert.False(HotkeyPolicy.Conflicts(b, a));
    }

    [Fact]
    public void Conflicts_WhenBothDisabled_ReturnsFalse()
    {
        var a = Binding(false, HotkeyModifiers.Control, "P");
        var b = Binding(false, HotkeyModifiers.Control, "P");

        Assert.False(HotkeyPolicy.Conflicts(a, b));
    }

    [Fact]
    public void Conflicts_MatchesKeysCaseInsensitively()
    {
        var a = Binding(true, HotkeyModifiers.Control, "P");
        var b = new HotkeyBinding(true, HotkeyModifiers.Control, "p");

        Assert.True(HotkeyPolicy.Conflicts(a, b));
    }
}
