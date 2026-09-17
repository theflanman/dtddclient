using DoesTheDogDie.Api;
using DoesTheDogDie.Statistics;
using Xunit;

namespace DoesTheDogDie.Tests.Statistics;

public class TriggerConfidenceTests
{
    private static TopicItemStat Stat(int yesSum, int noSum) => new()
    {
        TopicItemId = 1,
        YesSum = yesSum,
        NoSum = noSum,
        NumComments = 0,
        TopicId = 1,
        TopicName = "Dog dies",
        ItemId = 1,
    };

    [Fact]
    public void OldYeller_57_3_IsLikelyPresent()
    {
        var confidence = Stat(57, 3).ToConfidence();

        Assert.Equal(TriggerVerdict.LikelyPresent, confidence.Verdict);
        Assert.Equal(0.862930778084106, confidence.Lower, 1e-8);
        Assert.Equal(0.981845804024032, confidence.Upper, 1e-8);
    }

    [Fact]
    public void Zero_Zero_IsUncertain()
    {
        var confidence = TriggerConfidence.Compute(0, 0);

        Assert.Equal(TriggerVerdict.Uncertain, confidence.Verdict);
        Assert.Equal(0.025, confidence.Lower, 1e-8);
        Assert.Equal(0.975, confidence.Upper, 1e-8);
    }

    [Fact]
    public void Zero_50_IsLikelyAbsent()
    {
        var confidence = TriggerConfidence.Compute(0, 50);

        Assert.Equal(TriggerVerdict.LikelyAbsent, confidence.Verdict);
        Assert.Equal(0.0697770307495386, confidence.Upper, 1e-8);
        Assert.True(confidence.Upper < 0.5);
    }

    [Fact]
    public void ThresholdOverride()
    {
        var options = new ConfidenceOptions { DecisionThreshold = 0.9 };

        var confidence = TriggerConfidence.Compute(57, 3, options);

        Assert.Equal(TriggerVerdict.Uncertain, confidence.Verdict);
    }

    [Fact]
    public void IntervalMassOverride()
    {
        var wide = TriggerConfidence.Compute(57, 3, new ConfidenceOptions { IntervalMass = 0.95 });
        var narrow = TriggerConfidence.Compute(57, 3, new ConfidenceOptions { IntervalMass = 0.5 });

        Assert.True((narrow.Upper - narrow.Lower) < (wide.Upper - wide.Lower));
    }

    [Fact]
    public void ProbabilityPresent_IsPosteriorMean()
    {
        var confidence = TriggerConfidence.Compute(57, 3);

        Assert.Equal(confidence.Posterior.Mean, confidence.ProbabilityPresent, 1e-12);
    }

    [Fact]
    public void InvalidOptions_ThrowWithClearMessages()
    {
        // M6 regression test: IntervalMass/DecisionThreshold outside their valid ranges must fail fast, with a
        // message naming the offending option, rather than either silently misbehaving (DecisionThreshold) or
        // surfacing a confusing exception from deep inside BetaPosterior.CredibleInterval (IntervalMass).
        var badIntervalMass = Assert.Throws<ArgumentOutOfRangeException>(
            () => TriggerConfidence.Compute(57, 3, new ConfidenceOptions { IntervalMass = 0.0 }));
        Assert.Contains(nameof(ConfidenceOptions.IntervalMass), badIntervalMass.Message);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => TriggerConfidence.Compute(57, 3, new ConfidenceOptions { IntervalMass = 1.0 }));

        var badThreshold = Assert.Throws<ArgumentOutOfRangeException>(
            () => TriggerConfidence.Compute(57, 3, new ConfidenceOptions { DecisionThreshold = -0.1 }));
        Assert.Contains(nameof(ConfidenceOptions.DecisionThreshold), badThreshold.Message);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => TriggerConfidence.Compute(57, 3, new ConfidenceOptions { DecisionThreshold = 1.1 }));
    }
}
