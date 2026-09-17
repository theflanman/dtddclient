using System.Net;
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
    private readonly TaskCompletionSource _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Queue<DateTimeOffset> _sendTimestamps = new();

    private RateLimitStatus? _currentBudget;
    private DateTimeOffset? _exhaustedUntil;
    private int _disposedFlag;

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

    private bool IsDisposed => Volatile.Read(ref _disposedFlag) != 0;

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
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ThrowIfMonthlyExhausted();

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

            // TryWrite fails either because the channel is full, or because the writer was completed by
            // DisposeAsync racing with this call. Distinguish them so a caller doesn't see a misleading
            // "queue full" for what is actually shutdown.
            if (IsDisposed)
            {
                throw new ObjectDisposedException(nameof(ThrottledDtddClient));
            }

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
        // The try wraps the ENTIRE method body, not just the send/retry loop: the already-cancelled check,
        // the exhaustion/reserve checks (which invoke the user-supplied ThrottleOptions.MonthResetRule), and
        // CreateLinkedTokenSource are all capable of throwing, and previously sat outside any protection —
        // an exception from any of them would have left the job's TaskCompletionSource unset and faulted
        // the consumer loop. Nothing in this method may execute outside this try.
        try
        {
            if (job.Token.IsCancellationRequested)
            {
                job.Completion.TrySetCanceled(job.Token);
                return;
            }

            if (TryGetMonthlyExhaustedException(out var exhaustedException))
            {
                job.Completion.TrySetException(exhaustedException);
                return;
            }

            if (TryGetReserveExhaustedException(out var reserveException))
            {
                job.Completion.TrySetException(reserveException);
                return;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Token, _shutdownCts.Token);

            // Two attempts total: the original send, plus one retry after a minute-limit Retry-After wait.
            // The retry delay below deliberately lives OUTSIDE the inner try/catch (and thus is covered by
            // the single outer try/catch/finally below): a prior version awaited it inside the
            // DtddMinuteRateLimitException catch clause, guarded only by a catch for the caller's own
            // cancellation, so an OperationCanceledException from _shutdownCts (disposal) — or any other
            // exception, e.g. a negative RetryAfter reaching Task.Delay — escaped uncaught, left the job's
            // TaskCompletionSource unset, and (for anything but OCE) took the whole consumer loop down with it.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                DtddMinuteRateLimitException? minuteException = null;

                try
                {
                    await WaitForMinuteWindowAsync(linked.Token).ConfigureAwait(false);
                    var result = await job.Execute(linked.Token).ConfigureAwait(false);
                    job.Completion.TrySetResult(result);
                    return;
                }
                catch (DtddMinuteRateLimitException ex)
                {
                    UpdateBudget(ex.RateLimit);
                    minuteException = ex;
                }
                catch (DtddMonthlyRateLimitException ex)
                {
                    HandleMonthlyExhaustion(ex, job);
                    return;
                }

                if (attempt == 1)
                {
                    job.Completion.TrySetException(minuteException!);
                    return;
                }

                var delay = ClampNonNegative(minuteException!.RetryAfter ?? TimeSpan.FromSeconds(60));
                await Task.Delay(delay, _timeProvider, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (job.Token.IsCancellationRequested)
        {
            job.Completion.TrySetCanceled(job.Token);
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
            // Caused by _shutdownCts (disposal), not the caller's own token, and not an OperationCanceledException
            // coming from the inner client itself (e.g. an HttpClient timeout while the client is still live —
            // that one falls through to the catch below and surfaces to the caller unchanged): report it the
            // same way as a job that was still sitting in the queue when disposal drained it (see M3 /
            // RunLoopAsync above).
            job.Completion.TrySetException(new ObjectDisposedException(nameof(ThrottledDtddClient)));
        }
        catch (Exception ex)
        {
            if (ex is DtddApiException apiEx)
            {
                UpdateBudget(apiEx.RateLimit);
            }

            job.Completion.TrySetException(ex);
        }
        finally
        {
            // Absolute backstop: whatever happened above, never leave the caller's Task pending forever.
            // A no-op if the job was already completed (success, cancellation, or a specific failure) above.
            job.Completion.TrySetException(new ObjectDisposedException(nameof(ThrottledDtddClient)));
        }
    }

    /// <summary>
    /// Waits, if necessary, until sending would keep the in-house sliding window under the current per-minute
    /// limit, then records the send timestamp. Only ever called from the single consumer loop.
    /// </summary>
    private async Task WaitForMinuteWindowAsync(CancellationToken ct)
    {
        while (true)
        {
            var limit = GetEffectiveMinuteLimit();
            var now = _timeProvider.GetUtcNow();

            while (_sendTimestamps.Count > 0 && now - _sendTimestamps.Peek() >= TimeSpan.FromSeconds(60))
            {
                _sendTimestamps.Dequeue();
            }

            if (_sendTimestamps.Count < limit)
            {
                _sendTimestamps.Enqueue(now);
                return;
            }

            var wait = _sendTimestamps.Peek() + TimeSpan.FromSeconds(60) - now;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, _timeProvider, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The per-minute limit to pace against: the last-observed header value, unless it is missing or
    /// non-positive (e.g. a header literally reading <c>0</c>, which parses as <c>0</c> rather than null), in
    /// which case <see cref="ThrottleOptions.DefaultMinuteLimit"/> is used (falling back to <c>1</c> if that
    /// too is non-positive). Without this guard a non-positive limit would make the sliding window's
    /// <c>Count &lt; limit</c> check permanently false, spinning forever on an empty queue.
    /// </summary>
    private int GetEffectiveMinuteLimit()
    {
        var headerLimit = CurrentBudget?.MinuteLimit;
        if (headerLimit is { } limit && limit > 0)
        {
            return limit;
        }

        return _options.DefaultMinuteLimit > 0 ? _options.DefaultMinuteLimit : 1;
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

    /// <summary>
    /// Throws a fresh <see cref="DtddMonthlyRateLimitException"/>, synchronously and before anything is
    /// written to the queue, if the monthly budget is known to be exhausted until some future instant.
    /// </summary>
    private void ThrowIfMonthlyExhausted()
    {
        if (TryGetMonthlyExhaustedException(out var exception))
        {
            throw exception;
        }
    }

    /// <summary>
    /// Checks whether the monthly budget is known to be exhausted until some future instant. Called both at
    /// enqueue time (<see cref="ThrowIfMonthlyExhausted"/>) and again at dequeue time from
    /// <see cref="ProcessJobAsync"/>, because a call can pass the enqueue-time check and still land in the
    /// channel after a concurrent <see cref="HandleMonthlyExhaustion"/> has already drained it.
    /// </summary>
    private bool TryGetMonthlyExhaustedException(out DtddMonthlyRateLimitException exception)
    {
        DateTimeOffset? exhaustedUntil;
        lock (_stateLock)
        {
            exhaustedUntil = _exhaustedUntil;
        }

        if (exhaustedUntil is { } until)
        {
            var now = _timeProvider.GetUtcNow();
            if (now < until)
            {
                exception = new DtddMonthlyRateLimitException(
                    HttpStatusCode.TooManyRequests,
                    "monthly_limit_exceeded",
                    "Monthly request budget exhausted.",
                    CurrentBudget,
                    ClampNonNegative(until - now));
                return true;
            }
        }

        exception = null!;
        return false;
    }

    /// <summary>
    /// Checks the configured <see cref="ThrottleOptions.MonthlyReserve"/> against the last-observed
    /// <see cref="RateLimitStatus.MonthRemaining"/>, without making a request. Returns true (with an
    /// exception ready to fail the job) if sending now would dip into the reserve.
    /// </summary>
    private bool TryGetReserveExhaustedException(out DtddMonthlyRateLimitException exception)
    {
        var budget = CurrentBudget;
        if (budget is { MonthRemaining: { } remaining } && remaining - _options.MonthlyReserve <= 0)
        {
            var now = _timeProvider.GetUtcNow();
            var until = _options.MonthResetRule(now);
            exception = new DtddMonthlyRateLimitException(
                HttpStatusCode.TooManyRequests,
                "monthly_limit_exceeded",
                "Monthly reserve reached",
                budget,
                ClampNonNegative(until - now));
            return true;
        }

        exception = null!;
        return false;
    }

    /// <summary>
    /// Records the monthly budget as exhausted until the configured reset rule says otherwise, fails
    /// <paramref name="job"/> with <paramref name="ex"/>, and drains every job currently queued behind it,
    /// failing each with a fresh monthly exception carrying the time remaining until reset.
    /// </summary>
    private void HandleMonthlyExhaustion(DtddMonthlyRateLimitException ex, Job job)
    {
        UpdateBudget(ex.RateLimit);

        var until = _options.MonthResetRule(_timeProvider.GetUtcNow());
        lock (_stateLock)
        {
            _exhaustedUntil = until;
        }

        job.Completion.TrySetException(ex);

        while (_channel.Reader.TryRead(out var queued))
        {
            queued.Completion.TrySetException(new DtddMonthlyRateLimitException(
                ex.StatusCode,
                ex.ErrorCode,
                ex.Message,
                CurrentBudget,
                ClampNonNegative(until - _timeProvider.GetUtcNow())));
        }
    }

    private static TimeSpan ClampNonNegative(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposedFlag, 1, 0) != 0)
        {
            // Someone else is already disposing (or already finished); await the same outcome instead of
            // racing on _shutdownCts/_loopTask ourselves.
            await _disposeCompletion.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            _channel.Writer.TryComplete();
            await _shutdownCts.CancelAsync().ConfigureAwait(false);

            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: the loop's channel read observes _shutdownCts and unwinds via this exception.
                // Anything else escaping the loop is a genuine bug and should surface, not be swallowed.
            }
        }
        finally
        {
            // In the finally, not the try body: a faulted _loopTask await (a genuine bug, per above) must
            // not leak _shutdownCts or leave _disposeCompletion unset.
            _shutdownCts.Dispose();
            _disposeCompletion.TrySetResult();
        }
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
