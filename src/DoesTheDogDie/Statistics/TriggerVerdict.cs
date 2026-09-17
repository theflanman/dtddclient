namespace DoesTheDogDie.Statistics;

/// <summary>
/// A verdict on whether a trigger is present, derived from a <see cref="TriggerConfidence"/>'s
/// credible interval relative to a decision threshold.
/// </summary>
public enum TriggerVerdict
{
    /// <summary>The credible interval straddles the decision threshold.</summary>
    Uncertain,

    /// <summary>The credible interval's lower bound is above the decision threshold.</summary>
    LikelyPresent,

    /// <summary>The credible interval's upper bound is below the decision threshold.</summary>
    LikelyAbsent,
}
