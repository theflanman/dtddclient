using System.Globalization;
using DoesTheDogDie.Api;

namespace DoesTheDogDie.Cache;

/// <summary>The kind of identifier a <see cref="LookupKey"/> represents.</summary>
public enum LookupKind
{
    /// <summary>An IMDb id lookup.</summary>
    Imdb,

    /// <summary>A TMDB id lookup.</summary>
    Tmdb,

    /// <summary>An exact name (optionally with release year) lookup.</summary>
    Name,
}

/// <summary>
/// A normalized key identifying an exact (non-free-text) item lookup, suitable for caching the resolved
/// item id (or a cached negative result) independently of the item detail itself.
/// </summary>
/// <param name="Kind">Which kind of identifier <see cref="Key"/> represents.</param>
/// <param name="Key">The normalized identifier value.</param>
public sealed record LookupKey(LookupKind Kind, string Key)
{
    /// <summary>Creates a lookup key for an IMDb id, trimmed but otherwise unmodified.</summary>
    public static LookupKey Imdb(string imdbId) => new(LookupKind.Imdb, imdbId.Trim());

    /// <summary>Creates a lookup key for a TMDB id.</summary>
    public static LookupKey Tmdb(int tmdbId) => new(LookupKind.Tmdb, tmdbId.ToString(CultureInfo.InvariantCulture));

    /// <summary>Creates a lookup key for an exact name, optionally narrowed to a release year.</summary>
    public static LookupKey Name(string name, int? releaseYear) =>
        new(LookupKind.Name, name.Trim().ToLowerInvariant() + "|" + (releaseYear?.ToString(CultureInfo.InvariantCulture) ?? ""));

    /// <summary>
    /// Derives a <see cref="LookupKey"/> from an <see cref="ItemSearch"/>, or <see langword="null"/> when the
    /// search is a free-text query (<see cref="ItemSearchKind.Query"/>), which cannot be cached by exact key.
    /// </summary>
    public static LookupKey? FromSearch(ItemSearch search) => search.Kind switch
    {
        ItemSearchKind.Query => null,
        ItemSearchKind.Imdb => Imdb(search.ImdbId!),
        ItemSearchKind.Tmdb => Tmdb(search.TmdbId!.Value),
        ItemSearchKind.Name => Name(search.Name!, search.ReleaseYear),
        _ => throw new ArgumentOutOfRangeException(nameof(search), search.Kind, $"Unknown {nameof(ItemSearchKind)}."),
    };
}
