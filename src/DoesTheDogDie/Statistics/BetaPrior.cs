namespace DoesTheDogDie.Statistics;

/// <summary>
/// A Beta(Alpha, Beta) prior distribution over the probability that a trigger is present,
/// used as the conjugate prior for the Bernoulli vote model.
/// </summary>
public readonly record struct BetaPrior
{
    public BetaPrior(double alpha, double beta)
    {
        if (alpha <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(alpha), alpha, "Alpha must be positive.");
        }

        if (beta <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(beta), beta, "Beta must be positive.");
        }

        Alpha = alpha;
        Beta = beta;
    }

    public double Alpha { get; }

    public double Beta { get; }

    /// <summary>The uninformative Beta(1, 1) uniform prior.</summary>
    public static BetaPrior Uniform => new(1, 1);

    /// <summary>The Jeffreys Beta(0.5, 0.5) prior.</summary>
    public static BetaPrior Jeffreys => new(0.5, 0.5);
}
