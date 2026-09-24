using System.Collections.Concurrent;
using System.Net;
using DoesTheDogDie.Api;
using DoesTheDogDie.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace DoesTheDogDie.Tests;

/// <summary>
/// Priority classes on <see cref="ThrottledDtddClient"/>: interactive work overtakes queued background work,
/// each class stops at its own monthly floor, and background work that cannot be served this month is held
/// until the month resets rather than failed.
/// </summary>
/// <remarks>
/// "Background was held" is never asserted as a snapshot ("not completed yet"): that races the consumer loop,
/// and a broken implementation could pass simply because the loop had not reached the job. Instead the fake
/// stamps every call with the fake-clock time it arrived, and once the held job completes the test asserts it
/// reached the API no earlier than the reset.
/// <para>
/// Arrival stamps alone never make a correct client flaky, but they can let a broken one pass: if a client failed to
/// hold, its call could still land after the test advanced the clock. So a test holding work at a floor first waits
/// on <see cref="Sut.WaitUntilBackgroundHeldAsync"/>, which only a client that decided to hold can satisfy. Holds
/// armed by a 429 need no barrier, because the 429 arms them before the held job can be considered.
/// </para>
/// </remarks>
public class ThrottledDtddClientPriorityTests
{
    private static readonly DateTimeOffset StartTime = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ResetAt = ThrottleOptions.NextUtcMonthStart(StartTime);
    private static readonly TimeSpan Step = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    private static Sut CreateSut(ThrottleOptions? options = null)
    {
        var fake = new FakeApiClient();
        var time = new FakeTimeProvider(StartTime);
        var store = new MemoryBudgetStore();

        // With default options the client persists to `store`, which is what makes a hold observable (see
        // Sut.WaitUntilBackgroundHeldAsync). Tests passing their own options get no store and must not wait on one.
        var client = new ThrottledDtddClient(new KeyedApiClient(fake, Sut.BudgetId), options ?? new ThrottleOptions { BudgetStore = store }, time);
        var sut = new Sut(fake, time, client, client.ForPriority(RequestPriority.Background), store);
        fake.BeforeRespond = (call, _) =>
        {
            sut.Arrivals.Enqueue((call, time.GetUtcNow()));
            return Task.FromResult<Exception?>(null);
        };
        return sut;
    }

    private static DtddMonthlyRateLimitException Monthly429(FakeTimeProvider time) => new(
        HttpStatusCode.TooManyRequests,
        "monthly_limit_exceeded",
        "out of budget",
        new RateLimitStatus(30, 0, 5000, 0, time.GetUtcNow()),
        null);

    [Fact]
    public async Task Interactive_OvertakesQueuedBackground()
    {
        // Break: one FIFO queue across classes - background queued first would run first.
        var sut = CreateSut();
        await using var _ = sut.Client;
        var gate = new TaskCompletionSource();
        sut.Fake.BeforeRespond = async (call, _) =>
        {
            if (call == "Topics")
            {
                await gate.Task;
            }

            return null;
        };

        var busy = sut.Client.GetTopicsAsync();
        await AsyncAssert.WaitUntilAsync(() => sut.Fake.Calls.Count == 1);
        var background1 = sut.Background.GetItemTypesAsync();
        var background2 = sut.Background.GetTopicCategoriesAsync();
        var interactive = sut.Client.GetTopicSuperCategoriesAsync();
        gate.SetResult();
        await Task.WhenAll(busy, background1, background2, interactive);

        Assert.Equal(["Topics", "TopicSuperCategories", "ItemTypes", "TopicCategories"], sut.Fake.Calls);
    }

    [Fact]
    public async Task BackgroundAtFloor_HeldUntilReset_WhileInteractiveRuns()
    {
        // Break: background not held at its default floor of 500 remaining - it would reach the API before
        // the reset; or interactive wrongly held at that floor too.
        var sut = CreateSut();
        await using var _ = sut.Client;
        await sut.SeedMonthRemainingAsync(500);

        var held = sut.Background.GetItemTypesAsync();
        await sut.WaitUntilBackgroundHeldAsync();
        var interactive = await sut.Client.GetTopicCategoriesAsync();
        await sut.AdvanceToResetAsync(held);

        Assert.Equal(ResultSource.Live, interactive.Source);
        Assert.Equal(ResultSource.Live, (await held).Source);
        Assert.True(sut.ArrivalOf("TopicCategories") < ResetAt);
        Assert.True(sut.ArrivalOf("ItemTypes") >= ResetAt);
    }

    [Fact]
    public async Task Defaults_BackgroundRunsAbove500()
    {
        // Break: the background floor defaulting higher than 500, holding work that should run.
        var sut = CreateSut();
        await using var _ = sut.Client;
        await sut.SeedMonthRemainingAsync(501);

        var result = await sut.Background.GetItemTypesAsync().WaitAsync(HangGuard);

        Assert.Equal(ResultSource.Live, result.Source);
        Assert.True(sut.ArrivalOf("ItemTypes") < ResetAt);
    }

    [Fact]
    public async Task Defaults_InteractiveStopsAt50()
    {
        // Break: the interactive floor defaulting below 50, spending the buffer held for a future class.
        var sut = CreateSut();
        await using var _ = sut.Client;
        await sut.SeedMonthRemainingAsync(51);

        sut.Fake.NextRateLimit = new RateLimitStatus(30, 30, 5000, 50, sut.Time.GetUtcNow());
        var at51 = await sut.Client.GetItemTypesAsync();

        Assert.Equal(ResultSource.Live, at51.Source);
        await Assert.ThrowsAsync<DtddMonthlyRateLimitException>(() => sut.Client.GetTopicCategoriesAsync());
        Assert.DoesNotContain("TopicCategories", sut.Fake.Calls);
    }

    [Fact]
    public async Task Monthly429_FailsInteractive_HoldsBackground()
    {
        // Break: a monthly 429 draining background work along with interactive, as a single-class queue did.
        var sut = CreateSut();
        await using var _ = sut.Client;
        var gate = new TaskCompletionSource();
        sut.Fake.BeforeRespond = async (call, _) =>
        {
            sut.Arrivals.Enqueue((call, sut.Time.GetUtcNow()));
            if (call == "Topics")
            {
                await gate.Task;
                return Monthly429(sut.Time);
            }

            return null;
        };

        var busy = sut.Client.GetTopicsAsync();
        await AsyncAssert.WaitUntilAsync(() => sut.Fake.Calls.Count == 1);
        var queuedInteractive = sut.Client.GetItemTypesAsync();
        var queuedBackground = sut.Background.GetTopicCategoriesAsync();
        gate.SetResult();

        await Assert.ThrowsAsync<DtddMonthlyRateLimitException>(() => busy);
        await Assert.ThrowsAsync<DtddMonthlyRateLimitException>(() => queuedInteractive);
        await sut.AdvanceToResetAsync(queuedBackground);

        Assert.Equal(ResultSource.Live, (await queuedBackground).Source);
        Assert.DoesNotContain("ItemTypes", sut.Fake.Calls);
        Assert.True(sut.ArrivalOf("TopicCategories") >= ResetAt);
    }

    [Fact]
    public async Task Monthly429_OnBackgroundJob_HoldsThatJob()
    {
        // Break: the job that itself drew the 429 is failed rather than held - including by ProcessJobAsync's
        // finally-backstop, which fails any job still pending, and a re-held job is pending by design.
        //
        // Nothing here may depend on when the consumer handles the 429 relative to the clock advancing: a
        // background job's handling completes nothing the test can await, and the fake records each call before
        // its hook runs. So the 429 is chosen by call count, not by clock, and the reset instant is fixed rather
        // than computed from "now" - otherwise a 429 handled after the advance holds the job until the month
        // AFTER the reset. Both orderings then end the same way: retried no earlier than the reset.
        var sut = CreateSut(new ThrottleOptions { MonthResetRule = _ => ResetAt });
        await using var _ = sut.Client;
        var calls = 0;
        sut.Fake.BeforeRespond = (call, _) =>
        {
            sut.Arrivals.Enqueue((call, sut.Time.GetUtcNow()));
            return Task.FromResult<Exception?>(Interlocked.Increment(ref calls) == 1 ? Monthly429(sut.Time) : null);
        };

        var job = sut.Background.GetItemTypesAsync();
        await AsyncAssert.WaitUntilAsync(() => sut.Client.CurrentBudget?.MonthRemaining == 0);
        await sut.AdvanceToResetAsync(job);

        Assert.Equal(ResultSource.Live, (await job).Source);
        Assert.Equal(["ItemTypes", "ItemTypes"], sut.Fake.Calls);
        Assert.True(sut.Arrivals.Last().At >= ResetAt);
    }

    [Fact]
    public async Task Background_EnqueuedWhileExhausted_IsHeldNotRejected()
    {
        // Break: background enqueue applying interactive's fail-fast, throwing while the month is exhausted.
        var sut = CreateSut();
        await using var _ = sut.Client;
        sut.Fake.BeforeRespond = (call, _) =>
        {
            sut.Arrivals.Enqueue((call, sut.Time.GetUtcNow()));
            return Task.FromResult<Exception?>(sut.Time.GetUtcNow() < ResetAt && call == "Topics" ? Monthly429(sut.Time) : null);
        };
        await Assert.ThrowsAsync<DtddMonthlyRateLimitException>(() => sut.Client.GetTopicsAsync());

        var held = sut.Background.GetItemTypesAsync();
        await sut.AdvanceToResetAsync(held);

        Assert.Equal(ResultSource.Live, (await held).Source);
        Assert.True(sut.ArrivalOf("ItemTypes") >= ResetAt);
    }

    [Fact]
    public async Task QueueBound_IsPerClass()
    {
        // Break: one bound shared across classes, so a month of held background work locks out interactive
        // requests with DtddQueueFullException.
        var sut = CreateSut(new ThrottleOptions { MaxQueueLength = 1 });
        await using var scope = sut.Client;
        var gate = new TaskCompletionSource();
        sut.Fake.BeforeRespond = async (_, _) =>
        {
            await gate.Task;
            return null;
        };

        var busy = sut.Client.GetTopicsAsync();
        await AsyncAssert.WaitUntilAsync(() => sut.Fake.Calls.Count == 1);
        var background = sut.Background.GetItemTypesAsync();

        // Both throws are synchronous (before a Task is returned), so these are plain try/catch blocks.
        DtddQueueFullException? interactiveRejected = null;
        Task? interactive = null;
        try
        {
            interactive = sut.Client.GetTopicCategoriesAsync();
        }
        catch (DtddQueueFullException ex)
        {
            interactiveRejected = ex;
        }

        DtddQueueFullException? backgroundRejected = null;
        try
        {
            _ = sut.Background.GetTopicSuperCategoriesAsync();
        }
        catch (DtddQueueFullException ex)
        {
            backgroundRejected = ex;
        }

        gate.SetResult();

        Assert.Null(interactiveRejected);
        Assert.NotNull(backgroundRejected);
        await Task.WhenAll(busy, background, interactive!);
    }

    [Fact]
    public void Options_BackgroundFloorBelowInteractiveFloor_Throws()
    {
        // Break: accepting a configuration in which background work may spend the interactive reserve.
        Assert.Throws<ArgumentException>(() => new ThrottledDtddClient(
            new FakeApiClient(), new ThrottleOptions { MonthlyReserve = 100, BackgroundReserve = 99 }));
    }

    [Fact]
    public async Task ForPriority_UndefinedValue_Throws()
    {
        // Break: an out-of-range priority silently treated as one of the defined classes.
        var sut = CreateSut();
        await using var _ = sut.Client;

        Assert.Throws<ArgumentOutOfRangeException>(() => sut.Client.ForPriority((RequestPriority)42));
    }

    [Fact]
    public async Task Dispose_FailsHeldBackground()
    {
        // Break: held jobs left pending forever once the client is disposed.
        var sut = CreateSut();
        await sut.SeedMonthRemainingAsync(500);
        var held = sut.Background.GetItemTypesAsync();
        await sut.WaitUntilBackgroundHeldAsync();

        await sut.Client.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => held.WaitAsync(HangGuard));
        Assert.DoesNotContain("ItemTypes", sut.Fake.Calls);
    }

    [Fact]
    public async Task HeldBackground_Cancelled_CompletesCancelled()
    {
        // Break: holding a job losing its caller's cancellation, leaving a cancelled caller waiting a month.
        var sut = CreateSut();
        await using var _ = sut.Client;
        await sut.SeedMonthRemainingAsync(500);
        using var cts = new CancellationTokenSource();

        var held = sut.Background.GetItemTypesAsync(cts.Token);
        await sut.WaitUntilBackgroundHeldAsync();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => held.WaitAsync(HangGuard));
        Assert.DoesNotContain("ItemTypes", sut.Fake.Calls);
    }

    [Fact]
    public async Task BackgroundFloor_ResetRuleThrows_FailsTheJobNotTheLoop()
    {
        // Break: the user-supplied MonthResetRule throwing inside the consumer loop's own scheduling, killing
        // the loop so every later request hangs.
        var boom = new InvalidOperationException("boom");
        var sut = CreateSut(new ThrottleOptions { MonthResetRule = _ => throw boom });
        await using var _ = sut.Client;
        await sut.SeedMonthRemainingAsync(500);

        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.Background.GetItemTypesAsync().WaitAsync(HangGuard));
        var after = await sut.Client.GetTopicCategoriesAsync().WaitAsync(HangGuard);

        Assert.Same(boom, failed);
        Assert.Equal(ResultSource.Live, after.Source);
    }

    [Fact]
    public async Task BudgetFromLastMonth_DoesNotRefuseInteractiveThisMonth()
    {
        // Break: a MonthRemaining observed last month still counted this month. September's last response left
        // 50 (the interactive floor) and nothing checked it before the reset, so October's first request trips
        // the floor on a stale figure - and arms exhaustion until November.
        var sut = CreateSut();
        await using var _ = sut.Client;
        await sut.SeedMonthRemainingAsync(50);

        sut.Time.Advance(ResetAt - sut.Time.GetUtcNow() + TimeSpan.FromDays(1));
        sut.Fake.NextRateLimit = new RateLimitStatus(30, 30, 5000, 4999, sut.Time.GetUtcNow());
        var result = await sut.Client.GetItemTypesAsync().WaitAsync(HangGuard);

        Assert.Equal(ResultSource.Live, result.Source);
    }

    [Fact]
    public async Task BudgetFromLastMonth_DoesNotHoldBackgroundThisMonth()
    {
        // Break: as above for the background floor - September's 500 would hold October's background work
        // until November.
        var sut = CreateSut();
        await using var _ = sut.Client;
        await sut.SeedMonthRemainingAsync(500);

        sut.Time.Advance(ResetAt - sut.Time.GetUtcNow() + TimeSpan.FromDays(1));
        sut.Fake.NextRateLimit = new RateLimitStatus(30, 30, 5000, 4999, sut.Time.GetUtcNow());
        var result = await sut.Background.GetItemTypesAsync().WaitAsync(HangGuard);

        Assert.Equal(ResultSource.Live, result.Source);
        Assert.True(sut.ArrivalOf("ItemTypes") < ThrottleOptions.NextUtcMonthStart(ResetAt));
    }

    private sealed record Sut(FakeApiClient Fake, FakeTimeProvider Time, ThrottledDtddClient Client, IDtddClient Background, MemoryBudgetStore Store)
    {
        public const string BudgetId = "sut";

        /// <summary>
        /// Waits until the consumer has decided to hold background work. A hold is persisted before the consumer
        /// sleeps on it; a client that fails to hold never writes one, so this fails instead of letting that client
        /// pass by winning a race with the clock. Requires the default options (see <see cref="CreateSut"/>).
        /// </summary>
        public Task WaitUntilBackgroundHeldAsync() =>
            AsyncAssert.WaitUntilAsync(() => Store.Peek(BudgetId)?.BackgroundHeldUntil is not null);

        public ConcurrentQueue<(string Call, DateTimeOffset At)> Arrivals { get; } = new();

        public DateTimeOffset ArrivalOf(string call) => Arrivals.Single(a => a.Call == call).At;

        /// <summary>Makes the client observe <paramref name="remaining"/> monthly requests via one interactive call.</summary>
        public async Task SeedMonthRemainingAsync(int remaining)
        {
            Fake.NextRateLimit = new RateLimitStatus(30, 30, 5000, remaining, Time.GetUtcNow());
            await Client.GetTopicsAsync();
        }

        /// <summary>
        /// Moves the clock to the month reset, with a fresh month's budget, then keeps stepping until
        /// <paramref name="held"/> completes. Stepping, not a single advance, because the consumer registers its
        /// wake-up timer asynchronously (see <see cref="AsyncAssert"/>).
        /// </summary>
        public async Task AdvanceToResetAsync(Task held)
        {
            Fake.NextRateLimit = new RateLimitStatus(30, 30, 5000, 4999, ResetAt);
            Time.Advance(ResetAt - Time.GetUtcNow());
            await AsyncAssert.WaitUntilAsync(Time, Step, () => held.IsCompleted);
        }
    }
}
