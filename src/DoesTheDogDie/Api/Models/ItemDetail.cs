namespace DoesTheDogDie.Api;

/// <summary>
/// Item detail, including per-topic vote stats, as returned by the item detail endpoint.
/// </summary>
public sealed record ItemDetail : Item
{
    public IReadOnlyList<TopicItemStat> TopicItemStats { get; init; } = [];

    /// <summary>Structural equality; see <see cref="Item.Equals(Item?)"/> for why this is overridden.</summary>
    public bool Equals(ItemDetail? other) =>
        other is not null && base.Equals(other) && TopicItemStats.SequenceEqual(other.TopicItemStats);

    public override int GetHashCode() => base.GetHashCode();
}
