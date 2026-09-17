using System;
using DoesTheDogDie.Statistics;
using Xunit;

namespace DoesTheDogDie.Tests.Statistics;

public class BetaFunctionsTests
{
    [Theory]
    [InlineData(1.0, 0.0)]
    [InlineData(2.0, 0.0)]
    [InlineData(0.5, 0.5723649429247001)]
    [InlineData(10.0, 12.801827480081469)]
    public void LogGamma_KnownValues(double x, double expected)
    {
        Assert.Equal(expected, BetaFunctions.LogGamma(x), 1e-10);
    }

    [Theory]
    [InlineData(1.0, 1.0, 0.0)]
    [InlineData(2.0, 3.0, -2.484906649788)]
    [InlineData(58.0, 4.0, -14.5514394116195)]
    [InlineData(0.5, 0.5, 1.1447298858494)]
    [InlineData(100.0, 100.0, -139.665259086707)]
    public void LogBeta_MatchesScipy(double a, double b, double expected)
    {
        Assert.Equal(expected, BetaFunctions.LogBeta(a, b), 1e-10);
    }

    [Theory]
    [InlineData(1.0, 1.0, 0.3, 0.3)]
    [InlineData(2.0, 3.0, 0.5, 0.6875)]
    [InlineData(58.0, 4.0, 0.9, 0.128963220659291)]
    [InlineData(0.5, 0.5, 0.25, 0.333333333333333)]
    [InlineData(100.0, 100.0, 0.5, 0.5)]
    public void RegularizedIncompleteBeta_MatchesScipy(double a, double b, double x, double expected)
    {
        Assert.Equal(expected, BetaFunctions.RegularizedIncompleteBeta(a, b, x), 1e-8);
    }

    [Fact]
    public void RegularizedIncompleteBeta_XZero_ReturnsZero()
    {
        Assert.Equal(0.0, BetaFunctions.RegularizedIncompleteBeta(2.0, 3.0, 0.0));
    }

    [Fact]
    public void RegularizedIncompleteBeta_XOne_ReturnsOne()
    {
        Assert.Equal(1.0, BetaFunctions.RegularizedIncompleteBeta(2.0, 3.0, 1.0));
    }

    [Theory]
    [InlineData(0.0, 1.0, 0.5)]
    [InlineData(1.0, 0.0, 0.5)]
    [InlineData(1.0, 1.0, -0.1)]
    [InlineData(1.0, 1.0, 1.1)]
    public void RegularizedIncompleteBeta_InvalidArguments_Throws(double a, double b, double x)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BetaFunctions.RegularizedIncompleteBeta(a, b, x));
    }

    [Theory]
    [InlineData(1.0, 1.0, 0.025, 0.025)]
    [InlineData(1.0, 1.0, 0.5, 0.5)]
    [InlineData(1.0, 1.0, 0.975, 0.975)]
    [InlineData(2.0, 3.0, 0.025, 0.0675859864885429)]
    [InlineData(2.0, 3.0, 0.5, 0.38572756813239)]
    [InlineData(2.0, 3.0, 0.975, 0.805879550316757)]
    [InlineData(58.0, 4.0, 0.025, 0.862930778084106)]
    [InlineData(58.0, 4.0, 0.5, 0.940132728147312)]
    [InlineData(58.0, 4.0, 0.975, 0.981845804024032)]
    [InlineData(0.5, 0.5, 0.025, 0.00154133313343601)]
    [InlineData(0.5, 0.5, 0.5, 0.5)]
    [InlineData(0.5, 0.5, 0.975, 0.998458666866564)]
    [InlineData(100.0, 100.0, 0.025, 0.430950930941817)]
    [InlineData(100.0, 100.0, 0.5, 0.5)]
    [InlineData(100.0, 100.0, 0.975, 0.569049069058183)]
    public void InverseRegularizedIncompleteBeta_MatchesScipy(double a, double b, double p, double expected)
    {
        Assert.Equal(expected, BetaFunctions.InverseRegularizedIncompleteBeta(a, b, p), 1e-8);
    }

    [Fact]
    public void InverseRegularizedIncompleteBeta_PZero_ReturnsZero()
    {
        Assert.Equal(0.0, BetaFunctions.InverseRegularizedIncompleteBeta(2.0, 3.0, 0.0));
    }

    [Fact]
    public void InverseRegularizedIncompleteBeta_POne_ReturnsOne()
    {
        Assert.Equal(1.0, BetaFunctions.InverseRegularizedIncompleteBeta(2.0, 3.0, 1.0));
    }

    [Theory]
    [InlineData(0.0, 1.0, 0.5)]
    [InlineData(1.0, 0.0, 0.5)]
    [InlineData(1.0, 1.0, -0.1)]
    [InlineData(1.0, 1.0, 1.1)]
    public void InverseRegularizedIncompleteBeta_InvalidArguments_Throws(double a, double b, double p)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BetaFunctions.InverseRegularizedIncompleteBeta(a, b, p));
    }
}
