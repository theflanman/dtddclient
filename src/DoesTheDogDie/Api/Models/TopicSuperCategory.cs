namespace DoesTheDogDie.Api;

/// <summary>
/// A high-level grouping of topic categories (e.g. "Animals", "Mental Health").
/// </summary>
public sealed record TopicSuperCategory
{
    public required int Id { get; init; }

    public required string Name { get; init; }

    public required string ShortName { get; init; }
}
