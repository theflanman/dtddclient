namespace DoesTheDogDie.Cache;

/// <summary>
/// Tunable max-ages controlling when a cached entry is flagged as stale, and whether stale entries should
/// be refreshed in the background rather than blocking the caller.
/// </summary>
public sealed class CachePolicy
{
    /// <summary>Max age for taxonomy data (topics, item types, topic categories, topic super categories).</summary>
    public TimeSpan TaxonomyMaxAge { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Max age for cached <see cref="Api.ItemDetail"/> entries.</summary>
    public TimeSpan ItemMaxAge { get; init; } = TimeSpan.FromDays(30);

    /// <summary>Max age for cached ratings.</summary>
    public TimeSpan RatingsMaxAge { get; init; } = TimeSpan.FromDays(30);

    /// <summary>Max age for cached negative lookup results (an item that does not exist on DtDD).</summary>
    public TimeSpan NegativeLookupMaxAge { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Whether a stale entry should be refreshed in the background rather than blocking the caller.</summary>
    public bool RefreshStaleInBackground { get; init; } = true;

    /// <summary>The default policy, using the documented default max-ages.</summary>
    public static CachePolicy Default { get; } = new();
}
