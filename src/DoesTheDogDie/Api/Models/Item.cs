namespace DoesTheDogDie.Api;

/// <summary>
/// A movie, TV show, book, or other piece of media, as returned by the search endpoint (without stats).
/// </summary>
public record Item
{
    public required int Id { get; init; }

    public required string Name { get; init; }

    public IReadOnlyList<string> Genres { get; init; } = [];

    public int? ReleaseYear { get; init; }

    public required int ItemTypeId { get; init; }

    public required string ItemTypeName { get; init; }

    public int? TmdbId { get; init; }

    public string? ImdbId { get; init; }

    public string? BackgroundImage { get; init; }

    public string? PosterImage { get; init; }

    public string? Overview { get; init; }
}
