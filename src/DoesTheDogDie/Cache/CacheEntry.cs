namespace DoesTheDogDie.Cache;

/// <summary>
/// A cached value along with when it was fetched and whether it has aged past the applicable
/// <see cref="CachePolicy"/> max-age at read time.
/// </summary>
/// <typeparam name="T">The type of the cached value.</typeparam>
/// <param name="Value">The cached value.</param>
/// <param name="FetchedAt">When the value was originally fetched from the DtDD API.</param>
/// <param name="IsStale">
/// Whether the entry has aged past its applicable max age. Stale entries are still returned; the caller
/// decides whether to use them, refresh in the background, or wait for a fresh fetch.
/// </param>
public sealed record CacheEntry<T>(T Value, DateTimeOffset FetchedAt, bool IsStale);
