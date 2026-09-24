using System.Collections.Concurrent;
using System.Net;
using DoesTheDogDie.Api;
using DoesTheDogDie.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace DoesTheDogDie.Tests;

/// <summary>
/// Budget state surviving a restart through <see cref="ThrottleOptions.BudgetStore"/>. Each "restart" is a
/// second client over a fresh fake API, sharing nothing with the first but the store.
/// </summary>
public class ThrottledDtddClientBudgetStoreTests
{
    private static readonly DateTimeOffset StartTime = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ResetAt = ThrottleOptions.NextUtcMonthStart(StartTime);
    private static readonly TimeSpan Step = TimeSpan.FromMinutes(1);

    private static ThrottledDtddClient Open(IDtddApiClient api, IBudgetStore store, TimeProvider time) =>
        new(api, new ThrottleOptions { BudgetStore = store }, time);

    private static FakeApiClient Answering429WithoutHeaders() => new()
    {
        BeforeRespond = (_, _) => Task.FromResult<Exception?>(new DtddMonthlyRateLimitException(
            HttpStatusCode.TooManyRequests, "monthly_limit_exceeded", "out of budget", null, null)),
    };

    [Fact]
    public async Task Restart_AfterMonthly429_RefusesWithoutProbing()
    {
        // Break: the exhaustion deadline not persisted. This 429 carries no budget headers, so nothing but the
        // deadline tells a restarted client the month is spent; without it, it would probe the API again.
        var time = new FakeTimeProvider(StartTime);
        var store = new MemoryBudgetStore();
        await using (var first = Open(new KeyedApiClient(Answering429WithoutHeaders(), "key-a"), store, time))
        {
            await Assert.ThrowsAsync<DtddMonthlyRateLimitException>(() => first.GetTopicsAsync());
        }

        var fresh = new FakeApiClient();
        await using var second = Open(new KeyedApiClient(fresh, "key-a"), store, time);

        await Assert.ThrowsAsync<DtddMonthlyRateLimitException>(() => second.GetItemTypesAsync());
        Assert.Empty(fresh.Calls);
    }

    [Fact]
    public async Task Restart_WithAnotherKey_StartsFresh()
    {
        // Break: state not scoped to its key, so switching keys inherits the old key's exhaustion.
        var time = new FakeTimeProvider(StartTime);
        var store = new MemoryBudgetStore();
        await using (var first = Open(new KeyedApiClient(Answering429WithoutHeaders(), "key-a"), store, time))
        {
            await Assert.ThrowsAsync<DtddMonthlyRateLimitException>(() => first.GetTopicsAsync());
        }

        var fresh = new FakeApiClient();
        await using var second = Open(new KeyedApiClient(fresh, "key-b"), store, time);
        var result = await second.GetItemTypesAsync();

        Assert.Equal(ResultSource.Live, result.Source);
        Assert.Equal(["ItemTypes"], fresh.Calls);
    }

    [Fact]
    public async Task Restart_BudgetFigureRestored_HoldsBackgroundAtFloor()
    {
        // Break: the budget figure not persisted, so a restarted client knows nothing and lets background work
        // spend below its floor.
        var time = new FakeTimeProvider(StartTime);
        var store = new MemoryBudgetStore();
        var before = new FakeApiClient { NextRateLimit = new RateLimitStatus(30, 30, 5000, 500, StartTime) };
        await using (var first = Open(new KeyedApiClient(before, "key-a"), store, time))
        {
            await first.GetTopicsAsync();
        }

        var arrivals = new ConcurrentQueue<DateTimeOffset>();
        var fresh = new FakeApiClient();
        fresh.BeforeRespond = (_, _) =>
        {
            arrivals.Enqueue(time.GetUtcNow());
            return Task.FromResult<Exception?>(null);
        };
        await using var second = Open(new KeyedApiClient(fresh, "key-a"), store, time);
        var held = second.ForPriority(RequestPriority.Background).GetItemTypesAsync();

        // Barrier: the hold reaches the store before the consumer sleeps on it. Without it, a client that failed
        // to hold could still pass whenever the clock advance below beat its API call.
        await AsyncAssert.WaitUntilAsync(() => store.Peek("key-a")?.BackgroundHeldUntil is not null);
        fresh.NextRateLimit = new RateLimitStatus(30, 30, 5000, 4999, ResetAt);
        time.Advance(ResetAt - time.GetUtcNow());
        await AsyncAssert.WaitUntilAsync(time, Step, () => held.IsCompleted);

        Assert.Equal(ResultSource.Live, (await held).Source);
        Assert.True(Assert.Single(arrivals) >= ResetAt);
    }

    [Fact]
    public async Task Restart_AfterMonthRolledOver_DoesNotBlock()
    {
        // Break: a restored deadline re-anchored to the restart - e.g. recomputed from MonthResetRule(now) on
        // load - which would carry September's exhaustion into October and on to November.
        var time = new FakeTimeProvider(StartTime);
        var store = new MemoryBudgetStore();
        await using (var first = Open(new KeyedApiClient(Answering429WithoutHeaders(), "key-a"), store, time))
        {
            await Assert.ThrowsAsync<DtddMonthlyRateLimitException>(() => first.GetTopicsAsync());
        }

        time.Advance(ResetAt - time.GetUtcNow() + TimeSpan.FromDays(1));
        var fresh = new FakeApiClient();
        await using var second = Open(new KeyedApiClient(fresh, "key-a"), store, time);
        var result = await second.GetItemTypesAsync();

        Assert.Equal(ResultSource.Live, result.Source);
    }

    [Fact]
    public async Task UnidentifiedApi_NeverTouchesStore()
    {
        // Break: persisting under some default id when the API client names no key - a later key change would
        // then inherit that state.
        var store = new MemoryBudgetStore();
        await using (var client = Open(new FakeApiClient(), store, new FakeTimeProvider(StartTime)))
        {
            await client.GetTopicsAsync();
        }

        Assert.Equal(0, store.Accesses);
    }

    [Fact]
    public async Task StoreFailures_DoNotFailRequests()
    {
        // Break: a failing store failing requests. Persistence is an optimization; without it the client just
        // probes once after a restart, as it did before stores existed.
        var store = new MemoryBudgetStore { ThrowOnAccess = true };
        var client = Open(new KeyedApiClient(new FakeApiClient(), "key-a"), store, new FakeTimeProvider(StartTime));

        var result = await client.GetTopicsAsync();
        await client.DisposeAsync();

        Assert.Equal(ResultSource.Live, result.Source);
        Assert.True(store.Accesses > 0);
    }
}
