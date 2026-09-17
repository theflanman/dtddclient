namespace DoesTheDogDie.Statistics;

/// <summary>
/// Configures how a <see cref="TriggerConfidence"/> is computed from vote counts.
/// </summary>
public sealed class ConfidenceOptions
{
    /// <summary>The prior over the probability a trigger is present. Defaults to uniform.</summary>
    public BetaPrior Prior { get; init; } = BetaPrior.Uniform;

    /// <summary>The probability mass covered by the credible interval. Defaults to 0.95.</summary>
    public double IntervalMass { get; init; } = 0.95;

    /// <summary>
    /// The probability threshold used to decide the verdict: the trigger is judged likely present
    /// when the interval's lower bound exceeds this, likely absent when the upper bound is below it.
    /// </summary>
    public double DecisionThreshold { get; init; } = 0.5;

    public static ConfidenceOptions Default { get; } = new();
}
