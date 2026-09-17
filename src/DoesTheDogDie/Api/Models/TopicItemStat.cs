namespace DoesTheDogDie.Api.Models;

/// <summary>
/// Rolled-up yes/no vote totals and comment count for an item/topic pair.
/// </summary>
public sealed record TopicItemStat
{
    public required int TopicItemId { get; init; }

    public required int YesSum { get; init; }

    public required int NoSum { get; init; }

    public required int NumComments { get; init; }

    public required int TopicId { get; init; }

    public required string TopicName { get; init; }

    public required int ItemId { get; init; }
}
