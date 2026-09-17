namespace DoesTheDogDie.Api.Models;

/// <summary>
/// Item detail, including per-topic vote stats, as returned by the item detail endpoint.
/// </summary>
public sealed record ItemDetail : Item
{
    public IReadOnlyList<TopicItemStat> TopicItemStats { get; init; } = [];
}
