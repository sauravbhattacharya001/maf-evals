using EvalFramework.Calibration;
using EvalFramework.RagTriad;

namespace EvalFramework.Tests;

/// <summary>
/// Judge self-consistency. Comparing the judge with a human is meaningless until the judge agrees
/// with itself, and a metric that does not can still show excellent average agreement.
/// </summary>
public sealed class ConsistencyMetricsTests
{
    private static readonly TriadThresholds Thresholds = new();

    private static IReadOnlyList<ScorePair> Repeats(string metric, params double[] scores) =>
        scores.Select((score, index) => new ScorePair("case", metric, 5, score, index + 1)).ToArray();

    [Fact]
    public void AStableJudgeShowsNoSpreadAndNoFlips()
    {
        ConsistencySummary summary = ConsistencyMetrics
            .Summarize(Repeats(TriadMetrics.Groundedness, 5, 5, 5), Thresholds)
            .Single(item => item.Metric == TriadMetrics.Groundedness);

        Assert.Equal(0.0, summary.MeanStandardDeviation, 6);
        Assert.Equal(0.0, summary.WorstRange, 6);
        Assert.Equal(0.0, summary.VerdictFlipRate, 6);
    }

    [Fact]
    public void TheObservedRetrievalInstabilityIsDetected()
    {
        // The real measurement for cal-04: 5, 2, 4, 5, 2 from five identical requests.
        ConsistencySummary summary = ConsistencyMetrics
            .Summarize(Repeats(TriadMetrics.Relevance, 5, 2, 4, 5, 2), Thresholds)
            .Single(item => item.Metric == TriadMetrics.Relevance);

        Assert.Equal(3.0, summary.WorstRange, 6);
        Assert.True(summary.MeanStandardDeviation > 1.0);
        Assert.Equal(1.0, summary.VerdictFlipRate, 6);
    }

    [Fact]
    public void SpreadThatStaysOnOneSideOfTheFloorDoesNotFlipTheVerdict()
    {
        // Wobble alone is tolerable; only crossing the threshold changes a decision.
        ConsistencySummary summary = ConsistencyMetrics
            .Summarize(Repeats(TriadMetrics.Relevance, 5, 4, 5), Thresholds)
            .Single(item => item.Metric == TriadMetrics.Relevance);

        Assert.True(summary.WorstRange > 0);
        Assert.Equal(0.0, summary.VerdictFlipRate, 6);
    }

    [Fact]
    public void AdvisoryMetricsAreStillMeasuredEvenThoughTheyDoNotBlock()
    {
        ConsistencySummary summary = ConsistencyMetrics
            .Summarize(Repeats(TriadMetrics.Retrieval, 5, 2), Thresholds)
            .Single(item => item.Metric == TriadMetrics.Retrieval);

        Assert.Equal(2, summary.Cases[0].Scores.Count);
        Assert.True(summary.WorstRange > 0);
    }

    [Fact]
    public void StandardDeviationOfASingleObservationIsZeroNotUndefined()
    {
        Assert.Equal(0.0, ConsistencyMetrics.StandardDeviation([4]), 6);
        Assert.Equal(0.0, ConsistencyMetrics.StandardDeviation([]), 6);
    }

    [Fact]
    public void RetrievalIsAdvisoryAndGroundednessBlocks()
    {
        // Pins the calibration conclusions so a later edit cannot silently re-arm a noisy gate.
        Assert.False(Thresholds.Retrieval.Blocking);
        Assert.True(Thresholds.Groundedness.Blocking);
        Assert.True(Thresholds.Relevance.Blocking);
    }

    [Fact]
    public void GroundednessFloorSitsAboveTheScoreTheJudgeGivesFabrication()
    {
        // Calibration: cal-02, cal-06 and cal-11 were all scored exactly 3.0 by the judge despite
        // containing outright contradictions. A floor at or below 3.0 lets hallucination through.
        Assert.True(Thresholds.Groundedness.Floor > 3.0);
        Assert.Equal(TriadVerdict.Fail, Thresholds.Groundedness.Classify(3.0));
    }
}
