using DoesTheDogDie.Api;

namespace DoesTheDogDie.Statistics;

/// <summary>
/// Extension methods for computing <see cref="TriggerConfidence"/> from API models.
/// </summary>
public static class TopicItemStatExtensions
{
    /// <summary>
    /// Computes the confidence assessment for this topic/item's vote totals.
    /// </summary>
    public static TriggerConfidence ToConfidence(this TopicItemStat stat, ConfidenceOptions? options = null)
    {
        return TriggerConfidence.Compute(stat.YesSum, stat.NoSum, options);
    }
}
