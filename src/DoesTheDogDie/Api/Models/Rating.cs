namespace DoesTheDogDie.Api;

/// <summary>
/// A single report combining an item, a topic, a yes/no vote, and optional details about a specific
/// instance of a trigger in that item — such as a description, or timestamps.
/// </summary>
public sealed record Rating
{
    public required int Id { get; init; }

    public required int Yes { get; init; }

    public required int No { get; init; }

    public required int VoteSum { get; init; }

    public string? TriggerDescription { get; init; }

    public required bool IsRampant { get; init; }

    public required int Index1 { get; init; }

    public required int Index2 { get; init; }

    public int? Position1 { get; init; }

    public int? Position2 { get; init; }

    public int? Position3 { get; init; }

    public int? SafePosition1 { get; init; }

    public int? SafePosition2 { get; init; }

    public int? SafePosition3 { get; init; }

    public string? CueDescription { get; init; }

    public required int ItemId { get; init; }

    public required int TopicId { get; init; }

    public required bool IsSceneAlert { get; init; }
}
