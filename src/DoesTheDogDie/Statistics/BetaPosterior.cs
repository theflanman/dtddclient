using System;

namespace DoesTheDogDie.Statistics;

/// <summary>
/// The Beta(Alpha, Beta) posterior distribution over the probability that a trigger is present,
/// obtained by combining a <see cref="BetaPrior"/> with observed yes/no vote counts.
/// </summary>
public readonly record struct BetaPosterior
{
    public BetaPosterior(double alpha, double beta)
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

    /// <summary>
    /// Builds the posterior from observed yes/no vote counts and a prior (defaults to uniform).
    /// </summary>
    public static BetaPosterior FromCounts(int yes, int no, BetaPrior? prior = null)
    {
        if (yes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(yes), yes, "Yes count cannot be negative.");
        }

        if (no < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(no), no, "No count cannot be negative.");
        }

        var effectivePrior = prior ?? BetaPrior.Uniform;

        return new BetaPosterior(effectivePrior.Alpha + yes, effectivePrior.Beta + no);
    }

    public double Mean => Alpha / (Alpha + Beta);

    public double Variance
    {
        get
        {
            double sum = Alpha + Beta;
            return Alpha * Beta / (sum * sum * (sum + 1.0));
        }
    }

    /// <summary>The value x such that P(X &lt;= x) = p under this posterior.</summary>
    public double Quantile(double p) => BetaFunctions.InverseRegularizedIncompleteBeta(Alpha, Beta, p);

    /// <summary>
    /// An equal-tailed credible interval covering the given probability mass (default 95%).
    /// </summary>
    public (double Lower, double Upper) CredibleInterval(double mass = 0.95)
    {
        if (mass <= 0 || mass >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(mass), mass, "Mass must be strictly between 0 and 1.");
        }

        double tail = (1.0 - mass) / 2.0;
        return (Quantile(tail), Quantile(1.0 - tail));
    }
}
