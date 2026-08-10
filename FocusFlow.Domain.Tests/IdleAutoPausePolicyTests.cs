using FocusFlow.Domain.Models;
using FocusFlow.Domain.Services;

namespace FocusFlow.Domain.Tests;

/// <summary>
/// Pure logic — no fakes needed, everything IdleAutoPausePolicy needs is already on
/// <see cref="SessionState"/>.
/// </summary>
public class IdleAutoPausePolicyTests
{
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(3);

    private static SessionState State(TimerMode mode, bool isPaused = false) =>
        new() { Mode = mode, IsPaused = isPaused };

    [Fact]
    public void ShouldPause_WhenRunningAndIdleTimeMeetsThreshold_ReturnsTrue()
    {
        var result = IdleAutoPausePolicy.ShouldPause(State(TimerMode.Study), Threshold, Threshold);

        Assert.True(result);
    }

    [Fact]
    public void ShouldPause_WhenRunningAndIdleTimeExceedsThreshold_ReturnsTrue()
    {
        var result = IdleAutoPausePolicy.ShouldPause(
            State(TimerMode.Study), Threshold + TimeSpan.FromMinutes(5), Threshold);

        Assert.True(result);
    }

    [Fact]
    public void ShouldPause_WhenRunningAndIdleTimeIsBelowThreshold_ReturnsFalse()
    {
        var result = IdleAutoPausePolicy.ShouldPause(
            State(TimerMode.Study), Threshold - TimeSpan.FromSeconds(1), Threshold);

        Assert.False(result);
    }

    [Fact]
    public void ShouldPause_WhenIdle_ReturnsFalseEvenIfIdleTimeExceedsThreshold()
    {
        var result = IdleAutoPausePolicy.ShouldPause(
            State(TimerMode.Idle), Threshold + TimeSpan.FromMinutes(5), Threshold);

        Assert.False(result);
    }

    [Fact]
    public void ShouldPause_WhenAlreadyPaused_ReturnsFalse()
    {
        // Idempotent: a poll that lands after the session is already paused (by this
        // feature or manually) must not treat it as something new to act on.
        var result = IdleAutoPausePolicy.ShouldPause(
            State(TimerMode.Study, isPaused: true), Threshold + TimeSpan.FromMinutes(5), Threshold);

        Assert.False(result);
    }

    [Fact]
    public void ShouldPause_DuringABreak_StillApplies()
    {
        var result = IdleAutoPausePolicy.ShouldPause(State(TimerMode.Break), Threshold, Threshold);

        Assert.True(result);
    }

    [Fact]
    public void ShouldPause_WhenThresholdIsZeroOrNegative_ReturnsFalse()
    {
        // A hand-edited or corrupt config could set this to zero; treat that as "off"
        // rather than pausing on every poll.
        var result = IdleAutoPausePolicy.ShouldPause(State(TimerMode.Study), TimeSpan.FromMinutes(5), TimeSpan.Zero);

        Assert.False(result);
    }
}
