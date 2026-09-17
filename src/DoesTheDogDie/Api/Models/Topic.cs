namespace DoesTheDogDie.Api;

/// <summary>
/// A type of trigger or content warning (e.g. "a dog dies", "there are jump scares").
/// </summary>
public sealed record Topic
{
    public required int Id { get; init; }

    public required string Name { get; init; }

    public string? NotName { get; init; }

    public string? Keywords { get; init; }

    public string? Description { get; init; }

    public string? DoesName { get; init; }

    public string? ListName { get; init; }

    public string? MinimalName { get; init; }

    public required int TopicCategoryId { get; init; }

    public int? AltTopicCategoryId { get; init; }
}
