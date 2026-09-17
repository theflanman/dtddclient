namespace DoesTheDogDie.Api;

/// <summary>
/// The kind of media — movie, TV show, book, etc. Determines which index and position labels apply.
/// </summary>
public sealed record ItemType
{
    public required int Id { get; init; }

    public required string Name { get; init; }

    public required string Slug { get; init; }

    public required string Verb { get; init; }

    public required string PastTenseVerb { get; init; }

    public string? Index1Label { get; init; }

    public string? Index2Label { get; init; }

    public string? Position1Label { get; init; }

    public string? Position2Label { get; init; }

    public string? Position3Label { get; init; }
}
