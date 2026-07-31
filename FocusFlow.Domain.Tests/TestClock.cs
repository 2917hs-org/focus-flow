using Microsoft.Extensions.Time.Testing;

namespace FocusFlow.Domain.Tests;

internal static class TestClock
{
    /// <summary>
    /// Advances the fake clock as if the app were running normally.
    /// </summary>
    /// <remarks>
    /// Stepping matters. FakeTimeProvider.Advance jumps straight to the end of the
    /// interval and only then fires due callbacks, so a single Advance(1 minute) reaches
    /// the engine as one poll that is 60 seconds late — which is exactly what a suspended
    /// machine looks like, and the engine will (correctly, per FR-101) refuse to charge
    /// that time to the session. Small steps reproduce a poll loop that is actually
    /// keeping up. Use a bare Advance when the intent *is* to simulate sleep.
    /// </remarks>
    public static void Run(this FakeTimeProvider clock, TimeSpan duration)
    {
        var step = TimeSpan.FromSeconds(1);

        while (duration > TimeSpan.Zero)
        {
            var slice = duration < step ? duration : step;
            clock.Advance(slice);
            duration -= slice;
        }
    }
}
