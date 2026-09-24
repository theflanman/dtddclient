using System.Net;
using DoesTheDogDie.Api;

namespace DoesTheDogDie;

/// <summary>
/// A <see cref="IDtddClient"/> that serializes all calls through a single background consumer, pacing them
/// against the DtDD per-minute and monthly rate limits. This is the "work queue" layer: it does not cache
/// anything itself, but is safe to call concurrently from many threads.
/// </summary>
/// <remarks>
/// Calls made on this instance are <see cref="RequestPriority.Interactive"/>; <see cref="ForPriority"/> returns a
/// view that enqueues at another class. Interactive work is always served before queued background work. Each
/// class stops at its own monthly floor: interactive fails fast once it is reached, while background work is
/// held - still queued, its caller still waiting - until the month resets.
/// </remarks>
public sealed class ThrottledDtddClient : IDtddClient, IAsyncDisposable
{
    /// <summary>
    /// The longest the consumer sleeps before re-checking whether held background work may run. A single
    /// month-long timer, computed from a `now` read before the timer is created, overshoots whenever the clock
    /// moves in between - a system clock change, a suspended machine (Task.Delay does not count suspended time
    /// on every platform), or a test's fake clock. Re-checking the absolute deadline bounds that to one interval.
    /// </summary>
    private static readonly TimeSpan HeldRecheckInterval = TimeSpan.FromHours(1);

    private readonly IDtddApiClient _inner;
    private readonly ThrottleOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Task _loopTask;
    private readonly Lock _stateLock = new();
    private readonly TaskCompletionSource _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly PriorityView _backgroundView;

    // Two queues, not one ordered channel: holding background work needs to peek at it, put a job back at the
    // front after a 429, and drain interactive work alone - none of which a Channel supports. Guarded by
    // _stateLock, like everything below.
    private readonly LinkedList<Job> _interactiveQueue = new();
    private readonly LinkedList<Job> _backgroundQueue = new();

    private readonly Queue<DateTimeOffset> _sendTimestamps = new();

    private RateLimitStatus? _currentBudget;
    private DateTimeOffset? _exhaustedUntil;
    private DateTimeOffset? _backgroundHeldUntil;
    private DateTimeOffset? _budgetMonthEndsAt;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _addingCompleted;
    private int _disposedFlag;

    public ThrottledDtddClient(IDtddApiClient inner, ThrottleOptions? options = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(inner);

        _inner = inner;
        _options = options ?? new ThrottleOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (_options.BackgroundReserve < _options.MonthlyReserve)
        {
            throw new ArgumentException(
                $"{nameof(ThrottleOptions.BackgroundReserve)} ({_options.BackgroundReserve}) must be at least " +
                $"{nameof(ThrottleOptions.MonthlyReserve)} ({_options.MonthlyReserve}), or background work could " +
                "spend the interactive reserve.",
                nameof(options));
        }

        _backgroundView = new PriorityView(this, RequestPriority.Background);
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

    /// <summary>
    /// Returns an <see cref="IDtddClient"/> that enqueues every call on this client at
    /// <paramref name="priority"/>. <see cref="RequestPriority.Interactive"/> returns this instance itself. The
    /// view shares this client's queue, budget and lifetime; dispose this client, not the view.
    /// </summary>
    public IDtddClient ForPriority(RequestPriority priority) => priority switch
    {
        RequestPriority.Interactive => this,
        RequestPriority.Background => _backgroundView,
        _ => throw new ArgumentOutOfRangeException(nameof(priority), priority, "Unknown request priority."),
    };

    /// <inheritdoc />
    public Task<DtddResult<IReadOnlyList<Item>>> SearchItemsAsync(ItemSearch search, CancellationToken ct = default)
        => SearchItemsCoreAsync(search, RequestPriority.Interactive, ct);

    /// <inheritdoc />
    public Task<DtddResult<ItemDetail>> GetItemAsync(int itemId, CancellationToken ct = default)
        => GetItemCoreAsync(itemId, RequestPriority.Interactive, ct);

    /// <inheritdoc />
    public Task<DtddResult<IReadOnlyList<Rating>>> GetRatingsAsync(int itemId, int? topicId = null, CancellationToken ct = default)
        => GetRatingsCoreAsync(itemId, topicId, RequestPriority.Interactive, ct);

    /// <inheritdoc />
    public Task<DtddResult<IReadOnlyList<Topic>>> GetTopicsAsync(CancellationToken ct = default)
        => GetTopicsCoreAsync(RequestPriority.Interactive, ct);

    /// <inheritdoc />
    public Task<DtddResult<IReadOnlyList<ItemType>>> GetItemTypesAsync(CancellationToken ct = default)
        => GetItemTypesCoreAsync(RequestPriority.Interactive, ct);

    /// <inheritdoc />
    public Task<DtddResult<IReadOnlyList<TopicCategory>>> GetTopicCategoriesAsync(CancellationToken ct = default)
        => GetTopicCategoriesCoreAsync(RequestPriority.Interactive, ct);

    /// <inheritdoc />
    public Task<DtddResult<IReadOnlyList<TopicSuperCategory>>> GetTopicSuperCategoriesAsync(CancellationToken ct = default)
        => GetTopicSuperCategoriesCoreAsync(RequestPriority.Interactive, ct);

    private Task<DtddResult<IReadOnlyList<Item>>> SearchItemsCoreAsync(ItemSearch search, RequestPriority priority, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(search);
        return EnqueueAsync(c => _inner.SearchItemsAsync(search, c), priority, ct);
    }

    private Task<DtddResult<ItemDetail>> GetItemCoreAsync(int itemId, RequestPriority priority, CancellationToken ct)
        => EnqueueAsync(c => _inner.GetItemAsync(itemId, c), priority, ct);

    private Task<DtddResult<IReadOnlyList<Rating>>> GetRatingsCoreAsync(int itemId, int? topicId, RequestPriority priority, CancellationToken ct)
        => EnqueueAsync(c => _inner.GetRatingsAsync(itemId, topicId, c), priority, ct);

    private Task<DtddResult<IReadOnlyList<Topic>>> GetTopicsCoreAsync(RequestPriority priority, CancellationToken ct)
        => EnqueueAsync(c => _inner.GetTopicsAsync(c), priority, ct);

    private Task<DtddResult<IReadOnlyList<ItemType>>> GetItemTypesCoreAsync(RequestPriority priority, CancellationToken ct)
        => EnqueueAsync(c => _inner.GetItemTypesAsync(c), priority, ct);

    private Task<DtddResult<IReadOnlyList<TopicCategory>>> GetTopicCategoriesCoreAsync(RequestPriority priority, CancellationToken ct)
        => EnqueueAsync(c => _inner.GetTopicCategoriesAsync(c), priority, ct);

    private Task<DtddResult<IReadOnlyList<TopicSuperCategory>>> GetTopicSuperCategoriesCoreAsync(RequestPriority priority, CancellationToken ct)
        => EnqueueAsync(c => _inner.GetTopicSuperCategoriesAsync(c), priority, ct);

    /// <summary>
    /// Enqueues an API call to be served by the background consumer, and awaits its result.
    /// </summary>
    private Task<DtddResult<T>> EnqueueAsync<T>(
        Func<CancellationToken, Task<ApiResponse<T>>> call, RequestPriority priority, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        // Only interactive work fails fast on an exhausted month. Background work is held instead: it is
        // queued now and served once the month resets.
        if (priority == RequestPriority.Interactive)
        {
            ThrowIfMonthlyExhausted();
        }

        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

        var job = new Job
        {
            Priority = priority,
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

        bool disposed;
        bool full;
        lock (_stateLock)
        {
            var queue = QueueFor(priority);
            if (queue.Count >= _options.MaxQueueLength)
            {
                // Held background jobs can wait a month; one whose caller already cancelled should not keep
                // holding a slot until then.
                RemoveCompleted(queue);
            }

            disposed = _addingCompleted;
            full = !disposed && queue.Count >= _options.MaxQueueLength;
            if (!disposed && !full)
            {
                queue.AddLast(job);
                _wake.TrySetResult();
            }
        }

        if (disposed || full)
        {
            registration.Dispose();

            // Distinguish the two so a caller racing DisposeAsync doesn't see a misleading "queue full" for what
            // is actually shutdown.
            if (disposed)
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

    /// <summary>
    /// The single background consumer: runs the next eligible job, or waits for new work - or, while background
    /// work is held, for the month to reset.
    /// </summary>
    private async Task RunLoopAsync()
    {
        try
        {
            while (true)
            {
                _shutdownCts.Token.ThrowIfCancellationRequested();

                var step = TakeNextStep();
                if (step.Job is { } job)
                {
                    if (step.Failure is { } failure)
                    {
                        job.Completion.TrySetException(failure);
                    }
                    else
                    {
                        await ProcessJobAsync(job).ConfigureAwait(false);
                    }

                    continue;
                }

                await WaitForWorkAsync(step.Wake!, step.HeldFor).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down; fall through to drain below.
        }

        List<Job> leftovers;
        lock (_stateLock)
        {
            leftovers = [.. _interactiveQueue, .. _backgroundQueue];
            _interactiveQueue.Clear();
            _backgroundQueue.Clear();
        }

        foreach (var leftover in leftovers)
        {
            leftover.Completion.TrySetException(new ObjectDisposedException(nameof(ThrottledDtddClient)));
        }
    }

    /// <summary>
    /// Takes the next job to run - interactive first, then background unless background is held - or, when
    /// nothing may run, returns what to wait on instead.
    /// </summary>
    private NextStep TakeNextStep()
    {
        lock (_stateLock)
        {
            if (_interactiveQueue.First is { } interactive)
            {
                _interactiveQueue.RemoveFirst();
                return new NextStep(interactive.Value, null, null, null);
            }

            // A held job whose caller cancelled is already complete; don't let it hold the queue head for a month.
            while (_backgroundQueue.First is { } done && done.Value.Completion.Task.IsCompleted)
            {
                _backgroundQueue.RemoveFirst();
            }

            if (_backgroundQueue.First is { } background)
            {
                var now = _timeProvider.GetUtcNow();
                DateTimeOffset? heldUntil;
                try
                {
                    heldUntil = BackgroundHeldUntilLocked(now);
                }
                catch (Exception ex)
                {
                    // The user-supplied MonthResetRule threw. Fail the job that needed it rather than let the
                    // exception escape and kill the consumer loop, as ProcessJobAsync does for interactive work.
                    _backgroundQueue.RemoveFirst();
                    return new NextStep(background.Value, ex, null, null);
                }

                if (heldUntil is null)
                {
                    _backgroundQueue.RemoveFirst();
                    return new NextStep(background.Value, null, null, null);
                }

                var remaining = ClampNonNegative(heldUntil.Value - now);
                return new NextStep(null, null, CurrentWakeLocked(), remaining < HeldRecheckInterval ? remaining : HeldRecheckInterval);
            }

            return new NextStep(null, null, CurrentWakeLocked(), null);
        }
    }

    /// <summary>
    /// Waits until new work is enqueued or, if <paramref name="heldFor"/> is given, until held background work
    /// may run again - whichever comes first. Throws <see cref="OperationCanceledException"/> on shutdown.
    /// </summary>
    private async Task WaitForWorkAsync(Task wake, TimeSpan? heldFor)
    {
        if (heldFor is not { } delay)
        {
            await wake.WaitAsync(_shutdownCts.Token).ConfigureAwait(false);
            return;
        }

        using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
        try
        {
            var timer = Task.Delay(delay, _timeProvider, timerCts.Token);
            await Task.WhenAny(wake, timer).WaitAsync(_shutdownCts.Token).ConfigureAwait(false);
        }
        finally
        {
            // Woken early by new work: release the timer rather than leave it pending until the month resets.
            await timerCts.CancelAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The task the next enqueue will complete. Must be called while holding <see cref="_stateLock"/>.</summary>
    private Task CurrentWakeLocked()
    {
        if (_wake.Task.IsCompleted)
        {
            _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        return _wake.Task;
    }

    /// <summary>
    /// Returns when held background work may next run, or null if it may run now. Reaching the background floor
    /// holds background work until the month resets. Must be called while holding <see cref="_stateLock"/>.
    /// May throw, because it can invoke the user-supplied <see cref="ThrottleOptions.MonthResetRule"/>.
    /// </summary>
    private DateTimeOffset? BackgroundHeldUntilLocked(DateTimeOffset now)
    {
        ClearExpiredExhaustionLocked(now);

        if (_exhaustedUntil is { } exhausted && now < exhausted)
        {
            return exhausted;
        }

        if (_backgroundHeldUntil is { } held && now < held)
        {
            return held;
        }

        if (_currentBudget is { MonthRemaining: { } remaining } && remaining <= _options.BackgroundReserve)
        {
            var until = _options.MonthResetRule(now);
            _backgroundHeldUntil = until;
            return until;
        }

        return null;
    }

    private LinkedList<Job> QueueFor(RequestPriority priority) =>
        priority == RequestPriority.Background ? _backgroundQueue : _interactiveQueue;

    private static void RemoveCompleted(LinkedList<Job> queue)
    {
        for (var node = queue.First; node is not null;)
        {
            var next = node.Next;
            if (node.Value.Completion.Task.IsCompleted)
            {
                queue.Remove(node);
            }

            node = next;
        }
    }

    private async Task ProcessJobAsync(Job job)
    {
        // Set when a background job that drew a monthly 429 is put back in its queue. It is still pending by
        // design, so the finally-backstop below must leave it alone.
        var requeued = false;

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

            // Background work was only taken off its queue because TakeNextStep found it eligible, and nothing
            // but this single consumer changes the budget, so these checks apply to interactive work alone.
            if (job.Priority == RequestPriority.Interactive)
            {
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
                    requeued = HandleMonthlyExhaustion(ex, job);
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
            // A no-op if the job was already completed (success, cancellation, or a specific failure) above -
            // and skipped for a job put back in its queue, which is pending on purpose.
            if (!requeued)
            {
                job.Completion.TrySetException(new ObjectDisposedException(nameof(ThrottledDtddClient)));
            }
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

    /// <summary>
    /// Records the latest rate-limit figures, and when the month they describe ends. A MonthRemaining figure is
    /// only true for the month it was observed in: without that end instant, a figure at or under a floor from
    /// the last response of one month would trip that floor in the next and hold its work until the month after.
    /// </summary>
    private void UpdateBudget(RateLimitStatus? rateLimit)
    {
        if (rateLimit is null)
        {
            return;
        }

        // The throttle's own clock, not rateLimit.ObservedAt: one clock stays authoritative for every deadline.
        // If the user-supplied rule throws, the figure simply never expires - the behavior before this existed -
        // rather than failing every response; the rule still fails loudly where a floor or 429 needs it.
        DateTimeOffset? monthEndsAt;
        try
        {
            monthEndsAt = _options.MonthResetRule(_timeProvider.GetUtcNow());
        }
        catch (Exception)
        {
            monthEndsAt = null;
        }

        lock (_stateLock)
        {
            _currentBudget = rateLimit;
            _budgetMonthEndsAt = monthEndsAt;
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
    /// queue after a concurrent <see cref="HandleMonthlyExhaustion"/> has already drained it.
    /// </summary>
    private bool TryGetMonthlyExhaustedException(out DtddMonthlyRateLimitException exception)
    {
        var now = _timeProvider.GetUtcNow();
        DateTimeOffset? exhaustedUntil;
        RateLimitStatus? budget;
        lock (_stateLock)
        {
            ClearExpiredExhaustionLocked(now);
            exhaustedUntil = _exhaustedUntil;
            budget = _currentBudget;
        }

        if (exhaustedUntil is { } until && now < until)
        {
            exception = new DtddMonthlyRateLimitException(
                HttpStatusCode.TooManyRequests,
                "monthly_limit_exceeded",
                "Monthly request budget exhausted.",
                budget,
                ClampNonNegative(until - now));
            return true;
        }

        exception = null!;
        return false;
    }

    /// <summary>
    /// Checks the configured <see cref="ThrottleOptions.MonthlyReserve"/> against the last-observed
    /// <see cref="RateLimitStatus.MonthRemaining"/>, without making a request. Returns true (with an
    /// exception ready to fail the job) if sending now would dip into the reserve.
    /// </summary>
    /// <remarks>
    /// C1 fix: tripping this check now arms <see cref="_exhaustedUntil"/> exactly like a real monthly 429
    /// would (via <see cref="HandleMonthlyExhaustion"/>). Without that, a reserve trip never let a request
    /// through again — the only way <see cref="_currentBudget"/> could change was a response actually
    /// arriving, which the reserve check itself was permanently blocking, regardless of how much wall-clock
    /// time (including month rollovers) passed.
    /// </remarks>
    private bool TryGetReserveExhaustedException(out DtddMonthlyRateLimitException exception)
    {
        var now = _timeProvider.GetUtcNow();
        RateLimitStatus? budget;
        lock (_stateLock)
        {
            ClearExpiredExhaustionLocked(now);
            budget = _currentBudget;
        }

        if (budget is { MonthRemaining: { } remaining } && remaining - _options.MonthlyReserve <= 0)
        {
            var until = _options.MonthResetRule(now);
            lock (_stateLock)
            {
                _exhaustedUntil = until;
            }

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
    /// If an exhaustion or background-hold clock - or the end of the month the budget was observed in - has
    /// passed, clears it along with the stale
    /// <see cref="RateLimitStatus.MonthRemaining"/> figure that armed it (keeping
    /// <see cref="RateLimitStatus.MonthLimit"/> and the other fields), so exactly one probe request is allowed out
    /// to re-seed the budget from real headers. Without clearing it on a background hold too, last month's figure
    /// - still at or under the floor - would re-hold background work straight into the following month. Must be
    /// called while holding <see cref="_stateLock"/>.
    /// </summary>
    private void ClearExpiredExhaustionLocked(DateTimeOffset now)
    {
        var monthRolledOver = false;
        if (_exhaustedUntil is { } until && now >= until)
        {
            _exhaustedUntil = null;
            monthRolledOver = true;
        }

        if (_backgroundHeldUntil is { } held && now >= held)
        {
            _backgroundHeldUntil = null;
            monthRolledOver = true;
        }

        if (_budgetMonthEndsAt is { } monthEnds && now >= monthEnds)
        {
            _budgetMonthEndsAt = null;
            monthRolledOver = true;
        }

        if (monthRolledOver && _currentBudget is { } budget)
        {
            _currentBudget = budget with { MonthRemaining = null };
        }
    }

    /// <summary>
    /// Records the monthly budget as exhausted until the configured reset rule says otherwise, then fails
    /// interactive work - <paramref name="job"/> if it is interactive, and every interactive job queued behind
    /// it - with a monthly exception carrying the time remaining until reset. Background work is held instead:
    /// a background <paramref name="job"/> is put back at the front of its queue, and queued background jobs
    /// stay where they are, all to be served once the month resets.
    /// </summary>
    /// <returns>True if <paramref name="job"/> was put back in its queue, and so must not be completed.</returns>
    private bool HandleMonthlyExhaustion(DtddMonthlyRateLimitException ex, Job job)
    {
        UpdateBudget(ex.RateLimit);

        var until = _options.MonthResetRule(_timeProvider.GetUtcNow());
        List<Job> failed;
        var requeued = false;
        lock (_stateLock)
        {
            _exhaustedUntil = until;

            // Not after DisposeAsync has begun: the drain may already have run, which would strand the job.
            if (job.Priority == RequestPriority.Background && !_addingCompleted)
            {
                _backgroundQueue.AddFirst(job);
                requeued = true;
            }

            failed = [.. _interactiveQueue];
            _interactiveQueue.Clear();
        }

        if (!requeued)
        {
            job.Completion.TrySetException(ex);
        }

        foreach (var queued in failed)
        {
            queued.Completion.TrySetException(new DtddMonthlyRateLimitException(
                ex.StatusCode,
                ex.ErrorCode,
                ex.Message,
                CurrentBudget,
                ClampNonNegative(until - _timeProvider.GetUtcNow())));
        }

        return requeued;
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
            lock (_stateLock)
            {
                _addingCompleted = true;
                _wake.TrySetResult();
            }

            await _shutdownCts.CancelAsync().ConfigureAwait(false);

            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: the loop's wait observes _shutdownCts and unwinds via this exception.
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
        public required RequestPriority Priority { get; init; }

        public required Func<CancellationToken, Task<object>> Execute { get; init; }

        public required TaskCompletionSource<object> Completion { get; init; }

        public required CancellationToken Token { get; init; }
    }

    /// <summary>The boxed result of a successful job execution, before it is cast back to <c>T</c>.</summary>
    private sealed record JobSuccess(object? Value, DateTimeOffset FetchedAt);

    /// <summary>
    /// What the consumer loop should do next: run <see cref="Job"/> (or fail it with <see cref="Failure"/>), or
    /// wait on <see cref="Wake"/>, and on a timer of <see cref="HeldFor"/> if held background work is waiting.
    /// </summary>
    private readonly record struct NextStep(Job? Job, Exception? Failure, Task? Wake, TimeSpan? HeldFor);

    /// <summary>An <see cref="IDtddClient"/> that enqueues every call on its owner at one fixed priority.</summary>
    private sealed class PriorityView(ThrottledDtddClient owner, RequestPriority priority) : IDtddClient
    {
        public RateLimitStatus? CurrentBudget => owner.CurrentBudget;

        public Task<DtddResult<IReadOnlyList<Item>>> SearchItemsAsync(ItemSearch search, CancellationToken ct = default)
            => owner.SearchItemsCoreAsync(search, priority, ct);

        public Task<DtddResult<ItemDetail>> GetItemAsync(int itemId, CancellationToken ct = default)
            => owner.GetItemCoreAsync(itemId, priority, ct);

        public Task<DtddResult<IReadOnlyList<Rating>>> GetRatingsAsync(int itemId, int? topicId = null, CancellationToken ct = default)
            => owner.GetRatingsCoreAsync(itemId, topicId, priority, ct);

        public Task<DtddResult<IReadOnlyList<Topic>>> GetTopicsAsync(CancellationToken ct = default)
            => owner.GetTopicsCoreAsync(priority, ct);

        public Task<DtddResult<IReadOnlyList<ItemType>>> GetItemTypesAsync(CancellationToken ct = default)
            => owner.GetItemTypesCoreAsync(priority, ct);

        public Task<DtddResult<IReadOnlyList<TopicCategory>>> GetTopicCategoriesAsync(CancellationToken ct = default)
            => owner.GetTopicCategoriesCoreAsync(priority, ct);

        public Task<DtddResult<IReadOnlyList<TopicSuperCategory>>> GetTopicSuperCategoriesAsync(CancellationToken ct = default)
            => owner.GetTopicSuperCategoriesCoreAsync(priority, ct);
    }
}
