using FocusFlow.Domain.Models;
using FocusFlow.Domain.Services;

namespace FocusFlow.Domain.Tests;

/// <summary>
/// Pure logic — no fakes needed, everything AppBlockPolicy needs is already on
/// <see cref="SessionState"/>.
/// </summary>
public class AppBlockPolicyTests
{
    private static readonly string[] Blocked = ["com.apple.Safari"];

    private static SessionState State(TimerMode mode, bool isPaused = false) =>
        new() { Mode = mode, IsPaused = isPaused };

    [Fact]
    public void ShouldIntervene_WhenRunningAndUnpausedAndBundleIsBlocked_ReturnsTrue()
    {
        var result = AppBlockPolicy.ShouldIntervene(State(TimerMode.Study), Blocked, "com.apple.Safari");

        Assert.True(result);
    }

    [Fact]
    public void ShouldIntervene_WhenIdle_ReturnsFalseEvenIfBundleIsBlocked()
    {
        var result = AppBlockPolicy.ShouldIntervene(State(TimerMode.Idle), Blocked, "com.apple.Safari");

        Assert.False(result);
    }

    [Fact]
    public void ShouldIntervene_WhenPaused_ReturnsFalse()
    {
        var result = AppBlockPolicy.ShouldIntervene(
            State(TimerMode.Study, isPaused: true), Blocked, "com.apple.Safari");

        Assert.False(result);
    }

    [Fact]
    public void ShouldIntervene_WhenBundleIsNotBlocked_ReturnsFalse()
    {
        var result = AppBlockPolicy.ShouldIntervene(State(TimerMode.Study), Blocked, "com.apple.Notes");

        Assert.False(result);
    }

    [Fact]
    public void ShouldIntervene_MatchesBundleIdsCaseInsensitively()
    {
        var result = AppBlockPolicy.ShouldIntervene(State(TimerMode.Study), Blocked, "COM.APPLE.SAFARI");

        Assert.True(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ShouldIntervene_WhenActivatedBundleIsNullOrBlank_ReturnsFalse(string? activatedBundleId)
    {
        var result = AppBlockPolicy.ShouldIntervene(State(TimerMode.Study), Blocked, activatedBundleId);

        Assert.False(result);
    }

    [Fact]
    public void ShouldIntervene_DuringABreak_StillEnforces()
    {
        // Blocking isn't study-only — a break is still an active, unpaused session.
        var result = AppBlockPolicy.ShouldIntervene(State(TimerMode.Break), Blocked, "com.apple.Safari");

        Assert.True(result);
    }
}
