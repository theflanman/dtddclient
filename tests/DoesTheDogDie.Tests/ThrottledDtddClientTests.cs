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

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstTask);

        var secondException = await Record.ExceptionAsync(() => secondTask);
        Assert.True(
            secondException is OperationCanceledException or ObjectDisposedException,
            $"Expected cancellation or disposal, got {secondException?.GetType()}");
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
}
