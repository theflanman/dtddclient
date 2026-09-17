using Microsoft.Extensions.Time.Testing;

namespace DoesTheDogDie.Tests.Support;

/// <summary>
/// Small helper for deterministically waiting on async conditions in tests without real delays.
/// </summary>
internal static class AsyncAssert
{
    /// <summary>
    /// Repeatedly yields and checks <paramref name="condition"/> until it is true or
    /// <paramref name="maxIterations"/> is reached, in which case it fails the test (an unmet condition here
    /// is a real regression, not something a caller should be able to silently ignore by discarding a bool).
    /// </summary>
    public static async Task WaitUntilAsync(Func<bool> condition, int maxIterations = 1000)
    {
        for (var i = 0; i < maxIterations; i++)
        {
            if (condition())
            {
                return;
            }

            // A bare Task.Yield() reschedules onto the calling thread's local queue with priority, which can
            // starve a Task.Run-started background loop's work item sitting on the global queue under limited
            // scheduler parallelism (e.g. sandboxed/CPU-constrained CI). A tiny real delay forces a genuine
            // scheduling handoff instead. This is test-infrastructure polling only, not a stand-in for the
            // FakeTimeProvider-driven business-logic waits under test.
            await Task.Delay(1).ConfigureAwait(false);
        }

        if (!condition())
        {
            Assert.Fail($"Condition not met after {maxIterations} iterations.");
        }
    }

    /// <summary>
    /// Repeatedly advances <paramref name="time"/> by <paramref name="step"/> and yields real time, until
    /// <paramref name="condition"/> is true or <paramref name="maxIterations"/> is reached.
    /// </summary>
    /// <remarks>
    /// A single <c>time.Advance(step)</c> call only fires timers that already exist at the moment it runs.
    /// When the thing being waited on is a background loop that registers its own <see cref="TimeProvider"/>
    /// timer (e.g. a retry-after delay) asynchronously relative to the test thread, there is an inherent race
    /// between "the timer exists yet" and "the test calls Advance"; advancing exactly once is not reliable
    /// under scheduler pressure. Advancing repeatedly is: each call only ever moves the clock forward, so once
    /// the background timer is eventually registered (whenever that happens in real wall-clock time), a
    /// subsequent iteration's advance is guaranteed to reach its due time.
    /// </remarks>
    public static async Task WaitUntilAsync(FakeTimeProvider time, TimeSpan step, Func<bool> condition, int maxIterations = 200)
    {
        for (var i = 0; i < maxIterations; i++)
        {
            if (condition())
            {
                return;
            }

            time.Advance(step);
            await Task.Delay(1).ConfigureAwait(false);
        }

        if (!condition())
        {
            Assert.Fail($"Condition not met after {maxIterations} iterations (advancing by {step} each time).");
        }
    }
}
