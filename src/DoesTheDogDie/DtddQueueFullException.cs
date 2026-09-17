namespace DoesTheDogDie;

/// <summary>
/// Thrown when a request cannot be enqueued because the work queue has reached <see cref="ThrottleOptions.MaxQueueLength"/>.
/// </summary>
public sealed class DtddQueueFullException : InvalidOperationException
{
    public DtddQueueFullException(int maxLength)
        : base($"Request queue is full (max {maxLength}).")
    {
        MaxLength = maxLength;
    }

    /// <summary>The configured maximum queue length that was exceeded.</summary>
    public int MaxLength { get; }
}
