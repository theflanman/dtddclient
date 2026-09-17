namespace DoesTheDogDie.Api;

/// <summary>
/// A grouping of related topics (e.g. "Animal Death", "Drugs/Alcohol").
/// </summary>
public sealed record TopicCategory
{
    public required int Id { get; init; }

    public required string Name { get; init; }

    public required int TopicSuperCategoryId { get; init; }
}
