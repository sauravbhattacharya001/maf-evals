using EvalFramework.Calibration;
using EvalFramework.RagTriad;

namespace EvalFramework.Tests;

public sealed class AgreementMetricsTests
{
    private static readonly ThresholdBand Band = new(3.0, 4.0);

    private static ScorePair Pair(string id, double human, double? judge) =>
        new(id, TriadMetrics.Relevance, human, judge);

    private static MetricAgreement Compare(params ScorePair[] pairs) =>
        AgreementMetrics.Compare(TriadMetrics.Relevance, pairs, Band);

    [Fact]
    public void PerfectAgreementScoresPerfectly()
    {
        MetricAgreement agreement = Compare(Pair("a", 5, 5), Pair("b", 3, 3), Pair("c", 1, 1));

        Assert.Equal(1.0, agreement.ExactAgreement, 6);
        Assert.Equal(0.0, agreement.MeanAbsoluteError, 6);
        Assert.Equal(0.0, agreement.Bias, 6);
        Assert.Equal(1.0, agreement.BandAgreement, 6);
        Assert.Empty(agreement.Disagreements);
    }

    [Fact]
    public void ASystematicallyHarshJudgeCorrelatesPerfectlyYetStillBlocksGoodWork()
    {
        // The reason correlation alone is not enough: rankings match exactly, but every score is
        // two points low, so the judge would fail merges a human would pass.
        MetricAgreement agreement = Compare(Pair("a", 5, 3), Pair("b", 4, 2), Pair("c", 3, 1));

        Assert.Equal(1.0, agreement.Correlation, 6);
        Assert.Equal(-2.0, agreement.Bias, 6);
        Assert.NotEmpty(agreement.Disagreements);
    }

    [Fact]
    public void BiasIsSignedSoDirectionIsVisible()
    {
        Assert.True(Compare(Pair("a", 3, 5)).Bias > 0);
        Assert.True(Compare(Pair("a", 5, 3)).Bias < 0);
    }

    [Fact]
    public void OnlyGateChangingDifferencesAreListedAsDisagreements()
    {
        // 4 vs 5 differs but neither blocks, so it is not a disagreement that matters.
        MetricAgreement agreement = Compare(Pair("a", 5, 4), Pair("b", 4, 5));

        Assert.Empty(agreement.Disagreements);
        Assert.True(agreement.MeanAbsoluteError > 0);
    }

    [Fact]
    public void CrossingTheFloorInEitherDirectionIsADisagreement()
    {
        MetricAgreement agreement = Compare(Pair("judge-harsh", 4, 2), Pair("judge-lenient", 2, 4));

        Assert.Equal(2, agreement.Disagreements.Count);
    }

    [Fact]
    public void UnscoredCasesAreExcludedRatherThanCountedAsZero()
    {
        MetricAgreement agreement = Compare(Pair("a", 5, 5), Pair("b", 5, null));

        Assert.Equal(1, agreement.Compared);
        Assert.Equal(1.0, agreement.ExactAgreement, 6);
    }

    [Fact]
    public void WithinOneIsMoreForgivingThanExactAgreement()
    {
        MetricAgreement agreement = Compare(Pair("a", 5, 4), Pair("b", 3, 3));

        Assert.Equal(0.5, agreement.ExactAgreement, 6);
        Assert.Equal(1.0, agreement.WithinOne, 6);
    }

    [Fact]
    public void ConstantScoresGiveZeroCorrelationInsteadOfNaN()
    {
        // A judge that always answers 4 has no correlation; it must not poison the report with NaN.
        MetricAgreement agreement = Compare(Pair("a", 5, 4), Pair("b", 3, 4), Pair("c", 1, 4));

        Assert.Equal(0.0, agreement.Correlation, 6);
        Assert.False(double.IsNaN(agreement.Correlation));
    }

    [Fact]
    public void EmptyInputIsHandledWithoutDividingByZero()
    {
        MetricAgreement agreement = Compare();

        Assert.Equal(0, agreement.Compared);
        Assert.Equal(0.0, agreement.MeanAbsoluteError, 6);
    }
}
