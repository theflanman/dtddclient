using System;
using DoesTheDogDie.Statistics;
using Xunit;

namespace DoesTheDogDie.Tests.Statistics;

public class BetaPosteriorTests
{
    [Fact]
    public void FromCounts_Uniform_AddsOne()
    {
        var posterior = BetaPosterior.FromCounts(57, 3);

        Assert.Equal(58.0, posterior.Alpha, 1e-12);
        Assert.Equal(4.0, posterior.Beta, 1e-12);
    }

    [Fact]
    public void FromCounts_Jeffreys()
    {
        var posterior = BetaPosterior.FromCounts(57, 3, BetaPrior.Jeffreys);

        Assert.Equal(57.5, posterior.Alpha, 1e-12);
        Assert.Equal(3.5, posterior.Beta, 1e-12);
    }

    [Fact]
    public void Mean()
    {
        var posterior = BetaPosterior.FromCounts(57, 3);

        Assert.Equal(0.935483870967742, posterior.Mean, 1e-12);
    }

    [Fact]
    public void Variance()
    {
        var posterior = BetaPosterior.FromCounts(57, 3);

        Assert.Equal(0.00095799679566589, posterior.Variance, 1e-12);
    }

    [Fact]
    public void CredibleInterval95_MatchesScipy()
    {
        var posterior = BetaPosterior.FromCounts(57, 3);

        var (lower, upper) = posterior.CredibleInterval(0.95);

        Assert.Equal(0.862930778084106, lower, 1e-8);
        Assert.Equal(0.981845804024032, upper, 1e-8);
    }

    [Fact]
    public void Interval_Ordered()
    {
        var posterior = BetaPosterior.FromCounts(0, 0);

        var (lower, upper) = posterior.CredibleInterval();

        Assert.True(lower < upper);
    }

    [Fact]
    public void NegativeCounts_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BetaPosterior.FromCounts(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => BetaPosterior.FromCounts(0, -1));
    }

    [Fact]
    public void InvalidMass_Throws()
    {
        var posterior = BetaPosterior.FromCounts(1, 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => posterior.CredibleInterval(0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => posterior.CredibleInterval(1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => posterior.CredibleInterval(-0.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => posterior.CredibleInterval(1.1));
    }

    [Fact]
    public void NonPositiveAlphaOrBeta_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BetaPosterior(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BetaPosterior(1, 0));
    }
}

public class BetaPriorTests
{
    [Fact]
    public void Uniform_IsOneOne()
    {
        Assert.Equal(1.0, BetaPrior.Uniform.Alpha);
        Assert.Equal(1.0, BetaPrior.Uniform.Beta);
    }

    [Fact]
    public void Jeffreys_IsHalfHalf()
    {
        Assert.Equal(0.5, BetaPrior.Jeffreys.Alpha);
        Assert.Equal(0.5, BetaPrior.Jeffreys.Beta);
    }

    [Fact]
    public void NonPositive_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BetaPrior(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BetaPrior(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BetaPrior(-1, 1));
    }
}
