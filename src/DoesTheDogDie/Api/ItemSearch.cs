namespace DoesTheDogDie.Api;

/// <summary>
/// The kind of lookup an <see cref="ItemSearch"/> represents, i.e. which query parameter(s) it maps to.
/// </summary>
public enum ItemSearchKind
{
    /// <summary><c>?q=</c>, a free-text search term.</summary>
    Query,

    /// <summary><c>?imdb=</c>, an exact IMDb id lookup.</summary>
    Imdb,

    /// <summary><c>?tmdb=</c>, an exact TMDB id lookup.</summary>
    Tmdb,

    /// <summary><c>?name=</c> (optionally with <c>&amp;releaseYear=</c>), an exact name lookup.</summary>
    Name,
}

/// <summary>
/// A parameterization of a <c>GET /items</c> search request against the DtDD API.
/// </summary>
public sealed record ItemSearch
{
    private ItemSearch(ItemSearchKind kind, string? query, string? imdbId, int? tmdbId, string? name, int? releaseYear)
    {
        Kind = kind;
        Query = query;
        ImdbId = imdbId;
        TmdbId = tmdbId;
        Name = name;
        ReleaseYear = releaseYear;
    }

    /// <summary>Which kind of lookup this search represents.</summary>
    public ItemSearchKind Kind { get; }

    /// <summary>The free-text query term, when <see cref="Kind"/> is <see cref="ItemSearchKind.Query"/>.</summary>
    public string? Query { get; }

    /// <summary>The IMDb id, when <see cref="Kind"/> is <see cref="ItemSearchKind.Imdb"/>.</summary>
    public string? ImdbId { get; }

    /// <summary>The TMDB id, when <see cref="Kind"/> is <see cref="ItemSearchKind.Tmdb"/>.</summary>
    public int? TmdbId { get; }

    /// <summary>The exact item name, when <see cref="Kind"/> is <see cref="ItemSearchKind.Name"/>.</summary>
    public string? Name { get; }

    /// <summary>The optional release year filter, only used with <see cref="ItemSearchKind.Name"/>.</summary>
    public int? ReleaseYear { get; }

    /// <summary>Creates a free-text search by query term.</summary>
    public static ItemSearch ByQuery(string query)
    {
        RequireNonEmpty(query, nameof(query));
        return new ItemSearch(ItemSearchKind.Query, query, null, null, null, null);
    }

    /// <summary>Creates a search by exact IMDb id (e.g. <c>tt0050798</c>).</summary>
    public static ItemSearch ByImdbId(string imdbId)
    {
        RequireNonEmpty(imdbId, nameof(imdbId));
        return new ItemSearch(ItemSearchKind.Imdb, null, imdbId, null, null, null);
    }

    /// <summary>Creates a search by exact TMDB id.</summary>
    public static ItemSearch ByTmdbId(int tmdbId)
    {
        return new ItemSearch(ItemSearchKind.Tmdb, null, null, tmdbId, null, null);
    }

    /// <summary>Creates a search by exact name, optionally narrowed to a release year.</summary>
    public static ItemSearch ByName(string name, int? releaseYear = null)
    {
        RequireNonEmpty(name, nameof(name));
        return new ItemSearch(ItemSearchKind.Name, null, null, null, name, releaseYear);
    }

    /// <summary>
    /// Renders this search as a URL query string (including the leading <c>?</c>) suitable for appending to
    /// <c>/items</c>, with values percent-encoded.
    /// </summary>
    public string ToQueryString()
    {
        return Kind switch
        {
            ItemSearchKind.Query => $"?q={Uri.EscapeDataString(Query!)}",
            ItemSearchKind.Imdb => $"?imdb={Uri.EscapeDataString(ImdbId!)}",
            ItemSearchKind.Tmdb => $"?tmdb={TmdbId}",
            ItemSearchKind.Name => ReleaseYear is { } year
                ? $"?name={Uri.EscapeDataString(Name!)}&releaseYear={year}"
                : $"?name={Uri.EscapeDataString(Name!)}",
            _ => throw new InvalidOperationException($"Unknown {nameof(ItemSearchKind)}: {Kind}"),
        };
    }

    private static void RequireNonEmpty(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be empty or whitespace.", paramName);
        }
    }
}
