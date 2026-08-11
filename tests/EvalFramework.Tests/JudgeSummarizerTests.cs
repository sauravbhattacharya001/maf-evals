using EvalFramework.Judging;

namespace EvalFramework.Tests;

public sealed class JudgeSummarizerTests
{
    private static JudgedResponse Judged(string caseId, double relevance, double coherence) => new()
    {
        CaseId = caseId,
        Repetition = 1,
        Metrics =
        [
            new JudgeMetric("Relevance", relevance, "reason"),
            new JudgeMetric("Coherence", coherence, "reason")
        ]
    };

    [Fact]
    public void ScoresAreAggregatedPerMetric()
    {
        IReadOnlyList<MetricSummary> summary = JudgeSummarizer.Summarize(
            [Judged("a", 5, 4), Judged("b", 3, 4)]);

        MetricSummary relevance = summary.Single(metric => metric.Name == "Relevance");

        Assert.Equal(4.0, relevance.Mean, 6);
        Assert.Equal(3.0, relevance.Min, 6);
        Assert.Equal(2, relevance.Scored);
    }

    [Fact]
    public void UnscoredMetricsAreExcludedInsteadOfCountedAsZero()
    {
        JudgedResponse unscored = new()
        {
            CaseId = "c",
            Repetition = 1,
            Metrics = [new JudgeMetric("Relevance", null, "judge failed to parse")]
        };

        IReadOnlyList<MetricSummary> summary = JudgeSummarizer.Summarize([Judged("a", 5, 5), unscored]);

        Assert.Equal(1, summary.Single(metric => metric.Name == "Relevance").Scored);
    }

    [Fact]
    public void SingleBadResponseTripsTheGateDespiteAHealthyAverage()
    {
        IReadOnlyList<MetricSummary> summary = JudgeSummarizer.Summarize(
            [Judged("a", 5, 5), Judged("b", 5, 5), Judged("c", 2, 5)]);

        IReadOnlyList<string> violations = JudgeSummarizer.ApplyThresholds(summary, minMean: 4.0, minScore: 3.0);

        Assert.Contains(violations, violation => violation.Contains("Relevance minimum", StringComparison.Ordinal));
    }

    [Fact]
    public void StrongScoresProduceNoViolations()
    {
        IReadOnlyList<MetricSummary> summary = JudgeSummarizer.Summarize([Judged("a", 5, 5), Judged("b", 4, 4)]);

        Assert.Empty(JudgeSummarizer.ApplyThresholds(summary, minMean: 4.0, minScore: 3.0));
    }
}
