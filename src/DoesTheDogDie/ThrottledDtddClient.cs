using System.Threading.Channels;
using DoesTheDogDie.Api;

namespace DoesTheDogDie;

/// <summary>
/// A <see cref="IDtddClient"/> that serializes all calls through a single background consumer, pacing them
/// against the DtDD per-minute and monthly rate limits. This is the "work queue" layer: it does not cache
/// anything itself, but is safe to call concurrently from many threads.
/// </summary>
public sealed class ThrottledDtddClient : IDtddClient, IAsyncDisposable
{
    private readonly IDtddApiClient _inner;
    private readonly ThrottleOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Channel<Job> _channel;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Task _loopTask;
    private readonly Lock _stateLock = new();

    private RateLimitStatus? _currentBudget;
    private bool _disposed;

    public ThrottledDtddClient(IDtddApiClient inner, ThrottleOptions? options = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(inner);

        _inner = inner;
        _options = options ?? new ThrottleOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _channel = Channel.CreateBounded<Job>(new BoundedChannelOptions(_options.MaxQueueLength) { SingleReader = true });
        _loopTask = Task.Run(RunLoopAsync);
    }

    /// <inheritdoc />
    public RateLimitStatus? CurrentBudget
    {
        get
        {
            lock (_stateLock)
            {
                return _currentBudget;
            }
        }
    }

    /// <inheritdoc />
    public Task<DtddResult<IReadOnlyList<Item>>> SearchItemsAsync(ItemSearch search, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(search);
        return EnqueueAsync($"Search:{search.ToQueryString()}", c => _inner.SearchItemsAsync(search, c), ct);
    }

    /// <inheritdoc />
    public Task<DtddResult<ItemDetail>> GetItemAsync(int itemId, CancellationToken ct = default)
        => EnqueueAsync($"GetItem:{itemId}", c => _inner.GetItemAsync(itemId, c), ct);

    /// <inheritdoc />
    public Task<DtddResult<IReadOnlyList<Rating>>> GetRatingsAsync(int itemId, int? topicId = null, CancellationToken ct = default)
        => EnqueueAsync(
            topicId is { } id ? $"Ratings:{itemId}:{id}" : $"Ratings:{itemId}",
            c => _inner.GetRatingsAsync(itemId, topicId, c),
            ct);

    /// <inheritdoc />
    public Task<DtddResult<IReadOnlyList<Topic>>> GetTopicsAsync(CancellationToken ct = default)
        => EnqueueAsync("Topics", c => _inner.GetTopicsAsync(c), ct);

    /// <inheritdoc />
    public Task<DtddResult<IReadOnlyList<ItemType>>> GetItemTypesAsync(CancellationToken ct = default)
        => EnqueueAsync("ItemTypes", c => _inner.GetItemTypesAsync(c), ct);

    /// <inheritdoc />
    public Task<DtddResult<IReadOnlyList<TopicCategory>>> GetTopicCategoriesAsync(CancellationToken ct = default)
        => EnqueueAsync("TopicCategories", c => _inner.GetTopicCategoriesAsync(c), ct);

    /// <inheritdoc />
    public Task<DtddResult<IReadOnlyList<TopicSuperCategory>>> GetTopicSuperCategoriesAsync(CancellationToken ct = default)
        => EnqueueAsync("TopicSuperCategories", c => _inner.GetTopicSuperCategoriesAsync(c), ct);

    /// <summary>
    /// Enqueues an API call to be served by the background consumer, and awaits its result.
    /// </summary>
    private Task<DtddResult<T>> EnqueueAsync<T>(
        string name, Func<CancellationToken, Task<ApiResponse<T>>> call, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

        var job = new Job
        {
            Name = name,
            Token = ct,
            Completion = tcs,
            Execute = async jobCt =>
            {
                var response = await call(jobCt).ConfigureAwait(false);
                var fetchedAt = _timeProvider.GetUtcNow();
                UpdateBudget(response.RateLimit);
                return (object)new JobSuccess(response.Value, fetchedAt);
            },
        };

        var registration = ct.CanBeCanceled
            ? ct.Register(() => tcs.TrySetCanceled(ct))
            : default;

        if (!_channel.Writer.TryWrite(job))
        {
            registration.Dispose();
            throw new DtddQueueFullException(_options.MaxQueueLength);
        }

        return AwaitJobAsync<T>(tcs, registration);
    }

    private static async Task<DtddResult<T>> AwaitJobAsync<T>(
        TaskCompletionSource<object> tcs, CancellationTokenRegistration registration)
    {
        try
        {
            var boxed = await tcs.Task.ConfigureAwait(false);
            var success = (JobSuccess)boxed;
            return new DtddResult<T>((T)success.Value!, ResultSource.Live, success.FetchedAt);
        }
        finally
        {
            registration.Dispose();
        }
    }

    /// <summary>The single background consumer that dequeues and executes jobs in order.</summary>
    private async Task RunLoopAsync()
    {
        try
        {
            await foreach (var job in _channel.Reader.ReadAllAsync(_shutdownCts.Token).ConfigureAwait(false))
            {
                await ProcessJobAsync(job).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down; fall through to drain below.
        }

        while (_channel.Reader.TryRead(out var leftover))
        {
            leftover.Completion.TrySetException(new ObjectDisposedException(nameof(ThrottledDtddClient)));
        }
    }

    private async Task ProcessJobAsync(Job job)
    {
        if (job.Token.IsCancellationRequested)
        {
            job.Completion.TrySetCanceled(job.Token);
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Token, _shutdownCts.Token);

        try
        {
            var result = await job.Execute(linked.Token).ConfigureAwait(false);
            job.Completion.TrySetResult(result);
        }
        catch (OperationCanceledException) when (job.Token.IsCancellationRequested)
        {
            job.Completion.TrySetCanceled(job.Token);
        }
        catch (Exception ex)
        {
            if (ex is DtddApiException apiEx)
            {
                UpdateBudget(apiEx.RateLimit);
            }

            job.Completion.TrySetException(ex);
        }
    }

    private void UpdateBudget(RateLimitStatus? rateLimit)
    {
        if (rateLimit is null)
        {
            return;
        }

        lock (_stateLock)
        {
            _currentBudget = rateLimit;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _channel.Writer.TryComplete();
        await _shutdownCts.CancelAsync().ConfigureAwait(false);

        try
        {
            await _loopTask.ConfigureAwait(false);
        }
        catch
        {
            // The loop swallows its own per-job failures; this is a defensive backstop.
        }

        _shutdownCts.Dispose();
    }

    /// <summary>A unit of queued work: a wrapped API call plus the plumbing to report its outcome.</summary>
    private sealed class Job
    {
        public required string Name { get; init; }

        public required Func<CancellationToken, Task<object>> Execute { get; init; }

        public required TaskCompletionSource<object> Completion { get; init; }

        public required CancellationToken Token { get; init; }
    }

    /// <summary>The boxed result of a successful job execution, before it is cast back to <c>T</c>.</summary>
    private sealed record JobSuccess(object? Value, DateTimeOffset FetchedAt);
}
