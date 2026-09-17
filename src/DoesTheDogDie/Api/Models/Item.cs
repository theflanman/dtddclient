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

    /// <summary>
    /// Structural equality, overriding the record-synthesized member-wise comparison so that
    /// <see cref="Genres"/> is compared by content rather than by reference. This matters because a
    /// persistent cache (e.g. a SQLite-backed <see cref="Cache.IDtddCache"/>) round-trips values through
    /// JSON, always producing a fresh list instance even for equal content.
    /// </summary>
    public virtual bool Equals(Item? other) =>
        other is not null &&
        EqualityContract == other.EqualityContract &&
        Id == other.Id &&
        Name == other.Name &&
        Genres.SequenceEqual(other.Genres) &&
        ReleaseYear == other.ReleaseYear &&
        ItemTypeId == other.ItemTypeId &&
        ItemTypeName == other.ItemTypeName &&
        TmdbId == other.TmdbId &&
        ImdbId == other.ImdbId &&
        BackgroundImage == other.BackgroundImage &&
        PosterImage == other.PosterImage &&
        Overview == other.Overview;

    public override int GetHashCode() => HashCode.Combine(Id, Name, ItemTypeId, ItemTypeName, ReleaseYear);
}
