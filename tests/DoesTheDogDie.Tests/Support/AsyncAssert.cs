namespace DoesTheDogDie.Tests.Support;

/// <summary>
/// Small helper for deterministically waiting on async conditions in tests without real delays.
/// </summary>
internal static class AsyncAssert
{
    /// <summary>
    /// Repeatedly yields and checks <paramref name="condition"/> until it is true or
    /// <paramref name="maxIterations"/> is reached. Returns whether the condition became true.
    /// </summary>
    public static async Task<bool> WaitUntilAsync(Func<bool> condition, int maxIterations = 1000)
    {
        for (var i = 0; i < maxIterations; i++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Yield();
        }

        return condition();
    }
}
