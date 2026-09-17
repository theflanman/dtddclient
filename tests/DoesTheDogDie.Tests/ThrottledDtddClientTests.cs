using System.Net;
using DoesTheDogDie.Api;
using DoesTheDogDie.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace DoesTheDogDie.Tests;

public class ThrottledDtddClientTests
{
    private static readonly DateTimeOffset StartTime = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private static (FakeApiClient Fake, FakeTimeProvider Time, ThrottledDtddClient Client) CreateSut(
        ThrottleOptions? options = null)
    {
        var fake = new FakeApiClient();
        var time = new FakeTimeProvider(StartTime);
        var client = new ThrottledDtddClient(fake, options, time);
        return (fake, time, client);
    }

    [Fact]
    public async Task Calls_AreForwardedInOrder()
    {
        var (fake, _, client) = CreateSut();
        await using var _ = client;

        var t1 = client.GetTopicsAsync();
        var t2 = client.GetItemTypesAsync();
        var t3 = client.GetTopicCategoriesAsync();

        await Task.WhenAll(t1, t2, t3);

        Assert.Equal(["Topics", "ItemTypes", "TopicCategories"], fake.Calls);
    }

    [Fact]
    public async Task Result_IsLiveWithFetchedAt()
    {
        var (_, time, client) = CreateSut();
        await using var _ = client;

        var result = await client.GetTopicsAsync();

        Assert.Equal(ResultSource.Live, result.Source);
        Assert.Equal(time.GetUtcNow(), result.FetchedAt);
    }

    [Fact]
    public async Task CurrentBudget_UpdatesFromResponse()
    {
        var (fake, _, client) = CreateSut();
        await using var _ = client;

        fake.NextRateLimit = new RateLimitStatus(30, 25, 5000, 4900, StartTime);

        await client.GetTopicsAsync();

        Assert.Equal(fake.NextRateLimit, client.CurrentBudget);
    }

    [Fact]
    public async Task Dispose_CancelsPending()
    {
        var (fake, _, client) = CreateSut();

        var gate = new TaskCompletionSource();
        fake.BeforeRespond = async (_, ct) =>
        {
            await using var reg = ct.Register(() => gate.TrySetCanceled(ct));
            await gate.Task;
            return null;
        };

        var firstTask = client.GetTopicsAsync();
        await AsyncAssert.WaitUntilAsync(() => fake.Calls.Count == 1);

        var secondTask = client.GetItemTypesAsync();

        await client.DisposeAsync();

        // Both the in-flight job and the still-queued job must fail the same way: ObjectDisposedException,
        // regardless of whether disposal caught them mid-flight or drained them from the channel.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => firstTask);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => secondTask);
    }

    [Fact]
    public async Task Cancellation_BeforeSend_SkipsCall()
    {
        var (fake, _, client) = CreateSut();
        await using var _ = client;

        var gate = new TaskCompletionSource();
        fake.BeforeRespond = async (_, _) =>
        {
            await gate.Task;
            return null;
        };

        var firstTask = client.GetTopicsAsync();
        await AsyncAssert.WaitUntilAsync(() => fake.Calls.Count == 1);

        using var secondCts = new CancellationTokenSource();
        var secondTask = client.GetItemTypesAsync(secondCts.Token);
        await secondCts.CancelAsync();

        gate.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondTask);
        await firstTask;

        Assert.DoesNotContain("ItemTypes", fake.Calls);
    }

    [Fact]
    public async Task MinuteLimit_31stCallWaitsForWindow()
    {
        var (fake, time, client) = CreateSut();
        await using var _ = client;

        var tasks = Enumerable.Range(0, 31).Select(_ => client.GetTopicsAsync()).ToList();

        await AsyncAssert.WaitUntilAsync(() => fake.Calls.Count == 30);
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(1);
        }

        Assert.Equal(30, fake.Calls.Count);

        var start = time.GetUtcNow();
        var step = TimeSpan.FromSeconds(1);
        await AsyncAssert.WaitUntilAsync(time, step, () => fake.Calls.Count == 31);
        await Task.WhenAll(tasks);
        Assert.Equal(31, fake.Calls.Count);

        // The 31st call must not be served before a full 60s minute window has elapsed (and, since we poll in
        // 1s steps, not much later than that either — this is what stops the test from passing if the wait
        // were silently skipped or RetryAfter/window logic were broken).
        var elapsed = time.GetUtcNow() - start;
        Assert.InRange(elapsed, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60) + step);
    }

    [Fact]
    public async Task MinuteLimit_SeededFromHeader()
    {
        var (fake, time, client) = CreateSut();
        await using var _ = client;

        fake.NextRateLimit = new RateLimitStatus(5, 5, 5000, 5000, StartTime);

        var tasks = Enumerable.Range(0, 6).Select(_ => client.GetTopicsAsync()).ToList();

        await AsyncAssert.WaitUntilAsync(() => fake.Calls.Count == 5);
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(1);
        }

        Assert.Equal(5, fake.Calls.Count);

        var start = time.GetUtcNow();
        var step = TimeSpan.FromSeconds(1);
        await AsyncAssert.WaitUntilAsync(time, step, () => fake.Calls.Count == 6);
        await Task.WhenAll(tasks);

        var elapsed = time.GetUtcNow() - start;
        Assert.InRange(elapsed, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60) + step);
    }

    [Fact]
    public async Task Minute429_PausesForRetryAfterThenRetries()
    {
        var (fake, time, client) = CreateSut();
        await using var _ = client;

        var attempt = 0;
        fake.BeforeRespond = (_, _) =>
        {
            attempt++;
            Exception? result = attempt == 1
                ? new DtddMinuteRateLimitException(
                    HttpStatusCode.TooManyRequests, "rate_limit_exceeded", "slow down", null, TimeSpan.FromSeconds(10))
                : null;
            return Task.FromResult(result);
        };

        var task = client.GetTopicsAsync();

        await AsyncAssert.WaitUntilAsync(() => fake.Calls.Count == 1);
        Assert.False(task.IsCompleted);

        // A single time.Advance(10s) call only fires a retry-after timer that already exists; the background
        // consumer registers that timer asynchronously relative to this thread, so advance repeatedly (see
        // AsyncAssert.WaitUntilAsync(FakeTimeProvider, ...) for why a single call is not reliable here).
        var start = time.GetUtcNow();
        var step = TimeSpan.FromSeconds(1);
        await AsyncAssert.WaitUntilAsync(time, step, () => task.IsCompleted);
        await task;

        Assert.Equal(2, fake.Calls.Count);

        // Confirm the retry actually waited out the full RetryAfter (10s), rather than firing immediately —
        // i.e. that RetryAfter is not silently ignored.
        var elapsed = time.GetUtcNow() - start;
        Assert.InRange(elapsed, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10) + step);
    }

    [Fact]
    public async Task Minute429_TwiceThrows()
    {
        var (fake, time, client) = CreateSut();
        await using var _ = client;

        fake.BeforeRespond = (_, _) => Task.FromResult<Exception?>(
            new DtddMinuteRateLimitException(
                HttpStatusCode.TooManyRequests, "rate_limit_exceeded", "slow down", null, TimeSpan.FromSeconds(5)));

        var task = client.GetTopicsAsync();

        await AsyncAssert.WaitUntilAsync(() => fake.Calls.Count == 1);

        // See the comment in Minute429_PausesForRetryAfterThenRetries for why a single Advance() call is not
        // reliable here.
        var start = time.GetUtcNow();
        var step = TimeSpan.FromSeconds(1);
        await AsyncAssert.WaitUntilAsync(time, step, () => fake.Calls.Count == 2);

        await Assert.ThrowsAsync<DtddMinuteRateLimitException>(() => task);
        Assert.Equal(2, fake.Calls.Count);

        var elapsed = time.GetUtcNow() - start;
        Assert.InRange(elapsed, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5) + step);
    }

    [Fact]
    public async Task Monthly429_FailsCurrentAndQueued()
    {
        var (fake, _, client) = CreateSut();
        await using var _ = client;

        var gate = new TaskCompletionSource();
        fake.BeforeRespond = async (_, _) =>
        {
            await gate.Task;
            return new DtddMonthlyRateLimitException(
                HttpStatusCode.TooManyRequests, "monthly_limit_exceeded", "out of budget", null, null);
        };

        var firstTask = client.GetTopicsAsync();
        await AsyncAssert.WaitUntilAsync(() => fake.Calls.Count == 1);

        var queuedTask1 = client.GetItemTypesAsync();
        var queuedTask2 = client.GetTopicCategoriesAsync();

        gate.TrySetResult();

        await Assert.ThrowsAsync<DtddMonthlyRateLimitException>(() => firstTask);
        await Assert.ThrowsAsync<DtddMonthlyRateLimitException>(() => queuedTask1);
        await Assert.ThrowsAsync<DtddMonthlyRateLimitException>(() => queuedTask2);

        Assert.DoesNotContain("ItemTypes", fake.Calls);
        Assert.DoesNotContain("TopicCategories", fake.Calls);
    }

    [Fact]
    public async Task Monthly429_NewCallsFailUntilReset()
    {
        var (fake, time, client) = CreateSut();
        await using var _ = client;

        fake.BeforeRespond = (_, _) => Task.FromResult<Exception?>(
            new DtddMonthlyRateLimitException(
                HttpStatusCode.TooManyRequests, "monthly_limit_exceeded", "out of budget", null, null));

        await Assert.ThrowsAsync<DtddMonthlyRateLimitException>(() => client.GetTopicsAsync());

        // New calls should fail synchronously, without ever reaching the inner client, until the month resets.
        await Assert.ThrowsAsync<DtddMonthlyRateLimitException>(() => client.GetItemTypesAsync());
        Assert.DoesNotContain("ItemTypes", fake.Calls);

        fake.BeforeRespond = null;

        var resetAt = ThrottleOptions.NextUtcMonthStart(time.GetUtcNow());
        time.Advance(resetAt - time.GetUtcNow());

        var result = await client.GetTopicCategoriesAsync();
        Assert.Equal(ResultSource.Live, result.Source);
    }

    [Fact]
    public async Task Reserve_BlocksWhenRemainingAtReserve()
    {
        var (fake, _, client) = CreateSut(new ThrottleOptions { MonthlyReserve = 5 });
        await using var _ = client;

        fake.NextRateLimit = new RateLimitStatus(30, 30, 5000, 5, StartTime);

        var first = await client.GetTopicsAsync();
        Assert.Equal(ResultSource.Live, first.Source);
        Assert.Equal(5, client.CurrentBudget?.MonthRemaining);

        await Assert.ThrowsAsync<DtddMonthlyRateLimitException>(() => client.GetItemTypesAsync());
        Assert.DoesNotContain("ItemTypes", fake.Calls);
    }

    [Fact]
    public async Task QueueFull_Throws()
    {
        var (fake, _, client) = CreateSut(new ThrottleOptions { MaxQueueLength = 1 });
        await using var scope = client;

        var gate = new TaskCompletionSource();
        fake.BeforeRespond = async (_, _) =>
        {
            await gate.Task;
            return null;
        };

        var firstTask = client.GetTopicsAsync();
        await AsyncAssert.WaitUntilAsync(() => fake.Calls.Count == 1);

        var secondTask = client.GetItemTypesAsync();

        // The queue is full; GetTopicCategoriesAsync must throw synchronously (before returning a Task), not
        // via a faulted Task, so this is a plain try/catch rather than Assert.Throws(Async).
        DtddQueueFullException? thrown = null;
        try
        {
            _ = client.GetTopicCategoriesAsync();
        }
        catch (DtddQueueFullException ex)
        {
            thrown = ex;
        }

        Assert.NotNull(thrown);
        Assert.Equal(1, thrown.MaxLength);

        gate.TrySetResult();

        await firstTask;
        await secondTask;
    }

    [Fact]
    public async Task Dispose_DuringRetryAfterWait_FailsJob()
    {
        // C1 regression test: disposing while a job is inside its Retry-After wait must fail that job rather
        // than leaving its Task pending forever. Previously the retry delay lived inside a catch clause
        // guarded only by "the caller's own token was cancelled"; an OperationCanceledException coming from
        // _shutdownCts instead (i.e. disposal) escaped uncaught and never touched the job's TaskCompletionSource.
        var (fake, _, client) = CreateSut();

        fake.BeforeRespond = (_, _) => Task.FromResult<Exception?>(
            new DtddMinuteRateLimitException(
                HttpStatusCode.TooManyRequests, "rate_limit_exceeded", "slow down", null, TimeSpan.FromSeconds(30)));

        var task = client.GetTopicsAsync();

        await AsyncAssert.WaitUntilAsync(() => fake.Calls.Count == 1);

        // Give the background loop a moment to run the fully synchronous chain from "first attempt failed"
        // through "retry-after Task.Delay registered" (there is no observable signal for that point; this is
        // a scheduling nudge, not a business-logic wait — disposal is correct regardless of whether it lands
        // before or after this point, since every await in ProcessJobAsync uses the same linked token).
        await Task.Delay(20);

        await client.DisposeAsync();

        var exception = await Record.ExceptionAsync(() => task);
        Assert.IsType<ObjectDisposedException>(exception);
    }

    [Fact]
    public async Task NegativeRetryAfter_DoesNotKillLoop()
    {
        // I1 regression test: a negative RetryAfter must not propagate an unhandled ArgumentOutOfRangeException
        // out of Task.Delay and take the whole consumer loop down with it (which would hang every other job,
        // including ones enqueued afterward).
        var (fake, _, client) = CreateSut();
        await using var _ = client;

        fake.BeforeRespond = (_, _) => Task.FromResult<Exception?>(
            new DtddMinuteRateLimitException(
                HttpStatusCode.TooManyRequests, "rate_limit_exceeded", "slow down", null, TimeSpan.FromSeconds(-5)));

        var firstTask = client.GetTopicsAsync();

        // Negative RetryAfter clamps to zero, so the retry happens immediately; the fake keeps throwing, so
        // the second (and final) attempt fails the same way.
        var firstException = await Record.ExceptionAsync(() => firstTask);
        Assert.IsType<DtddMinuteRateLimitException>(firstException);

        // The loop must still be alive: a subsequent call is processed normally.
        fake.BeforeRespond = null;
        var secondResult = await client.GetItemTypesAsync();
        Assert.Equal(ResultSource.Live, secondResult.Source);
    }

    [Fact]
    public async Task MinuteLimitZero_FallsBackToDefault()
    {
        // M2 regression test: a header reporting MinuteLimit 0 parses as 0, not null, so a naive
        // "?? DefaultMinuteLimit" does nothing. Without a guard, the sliding window's "count < limit" check
        // is permanently false against an empty window, and WaitForMinuteWindowAsync spins forever on
        // Peek() of an empty queue.
        var (fake, _, client) = CreateSut();
        await using var _ = client;

        fake.NextRateLimit = new RateLimitStatus(0, 0, 5000, 5000, StartTime);

        var first = await client.GetTopicsAsync();
        Assert.Equal(ResultSource.Live, first.Source);
        Assert.Equal(0, client.CurrentBudget?.MinuteLimit);

        var second = await client.GetItemTypesAsync();
        Assert.Equal(ResultSource.Live, second.Source);
    }
}
