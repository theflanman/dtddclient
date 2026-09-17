namespace DoesTheDogDie;

/// <summary>
/// Where a <see cref="DtddResult{T}"/>'s value came from.
/// </summary>
public enum ResultSource
{
    /// <summary>Fetched from the DtDD API for this call.</summary>
    Live,

    /// <summary>Served from the cache without a fresh fetch, within its freshness window.</summary>
    Cache,

    /// <summary>Served from the cache but past its freshness window; the caller decides whether to trust it.</summary>
    StaleCache,
}
