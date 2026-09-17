namespace DoesTheDogDie.Statistics;

/// <summary>
/// The confidence assessment for a single trigger's presence in an item, derived from its
/// Beta-Bernoulli posterior over yes/no votes.
/// </summary>
public sealed record TriggerConfidence(BetaPosterior Posterior, double Lower, double Upper, TriggerVerdict Verdict)
{
    /// <summary>The posterior mean probability that the trigger is present.</summary>
    public double ProbabilityPresent => Posterior.Mean;

    /// <summary>
    /// Computes the confidence assessment from observed yes/no vote counts.
    /// </summary>
    public static TriggerConfidence Compute(int yes, int no, ConfidenceOptions? options = null)
    {
        var effectiveOptions = options ?? ConfidenceOptions.Default;

        if (effectiveOptions.IntervalMass <= 0 || effectiveOptions.IntervalMass >= 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), effectiveOptions.IntervalMass, $"{nameof(ConfidenceOptions.IntervalMass)} must be strictly between 0 and 1.");
        }

        if (effectiveOptions.DecisionThreshold < 0 || effectiveOptions.DecisionThreshold > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), effectiveOptions.DecisionThreshold, $"{nameof(ConfidenceOptions.DecisionThreshold)} must be in [0, 1].");
        }

        var posterior = BetaPosterior.FromCounts(yes, no, effectiveOptions.Prior);
        var (lower, upper) = posterior.CredibleInterval(effectiveOptions.IntervalMass);

        TriggerVerdict verdict;
        if (lower > effectiveOptions.DecisionThreshold)
        {
            verdict = TriggerVerdict.LikelyPresent;
        }
        else if (upper < effectiveOptions.DecisionThreshold)
        {
            verdict = TriggerVerdict.LikelyAbsent;
        }
        else
        {
            verdict = TriggerVerdict.Uncertain;
        }

        return new TriggerConfidence(posterior, lower, upper, verdict);
    }
}
