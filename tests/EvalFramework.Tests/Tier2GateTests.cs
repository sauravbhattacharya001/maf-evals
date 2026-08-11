using EvalFramework.Datasets;
using EvalFramework.Execution;
using EvalFramework.RagTriad;
using EvalFramework.Retrieval;
using EvalFramework.Rules;
using EvalFramework.Statistics;

namespace EvalFramework.Tests;

public sealed class Tier2GateTests
{
    private static readonly TriadThresholds Thresholds = new();

    private static GoldenCase Case(string id, params string[] expectedChunks) => new()
    {
        Id = id,
        Query = "q",
        MinLength = 1,
        RequireActionableFormat = false,
        ExpectedChunkIds = expectedChunks
    };

    private static ResponseRecord Record(
        GoldenCase goldenCase,
        string response = "a perfectly adequate answer",
        string[]? retrievedChunks = null,
        bool blocked = false) => new()
        {
            CaseId = goldenCase.Id,
            Query = goldenCase.Query,
            Repetition = 1,
            Response = response,
            LatencyMs = 10,
            Rules = ResponseRules.Evaluate(goldenCase.ToRuleSet(), response),
            Blocked = blocked,
            Retrieval = new RetrievalTrace(
                goldenCase.Query,
                (retrievedChunks ?? []).Select(id => new RetrievedChunk(id, id, "text", 1)).ToArray())
        };

    private static RunArtifact Run(IReadOnlyList<GoldenCase> cases, params ResponseRecord[] records) =>
        AgentRunner.Build(cases, records, 1, "tier2", "test-model", "test.jsonl");

    private static TriadResult Triad(string caseId, double retrieval, double grounded, double relevance) =>
        new(caseId,
        [
            new TriadScore(TriadMetrics.Retrieval, retrieval, Thresholds.Retrieval.Classify(retrieval), null),
            new TriadScore(TriadMetrics.Groundedness, grounded, Thresholds.Groundedness.Classify(grounded), null),
            new TriadScore(TriadMetrics.Relevance, relevance, Thresholds.Relevance.Classify(relevance), null)
        ]);

    [Fact]
    public void HealthyRunPasses()
    {
        GoldenCase good = Case("good", "refunds#1");
        Tier2Result result = Tier2Gate.Apply(
            Run([good], Record(good, retrievedChunks: ["refunds#1"])),
            [good],
            [Triad("good", 5, 5, 5)],
            Thresholds);

        Assert.True(result.Passed);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ScoreBetweenFloorAndTargetWarnsInsteadOfBlocking()
    {
        // The whole point of the band: a mediocre judge score must not flake a pull request.
        GoldenCase good = Case("good");
        Tier2Result result = Tier2Gate.Apply(
            Run([good], Record(good)),
            [good],
            [Triad("good", 3.5, 3.5, 3.5)],
            Thresholds);

        Assert.True(result.Passed);
        Assert.Equal(3, result.Warnings.Count);
    }

    [Fact]
    public void ScoreBelowTheFloorBlocks()
    {
        GoldenCase good = Case("good");
        Tier2Result result = Tier2Gate.Apply(
            Run([good], Record(good)),
            [good],
            [Triad("good", 5, 1.5, 5)],
            Thresholds);

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, violation => violation.Gate == "triad");
    }

    [Fact]
    public void MissingExpectedChunkFailsWithoutNeedingAJudge()
    {
        GoldenCase expecting = Case("expecting", "refunds#1");
        Tier2Result result = Tier2Gate.Apply(
            Run([expecting], Record(expecting, retrievedChunks: ["shipping#2"])),
            [expecting],
            [],
            Thresholds);

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, violation => violation.Gate == "retrieval");
    }

    [Fact]
    public void BrokenRuleBlocksTheMerge()
    {
        GoldenCase strict = new()
        {
            Id = "strict",
            Query = "q",
            MinLength = 500,
            RequireActionableFormat = false
        };

        Tier2Result result = Tier2Gate.Apply(Run([strict], Record(strict)), [strict], [], Thresholds);

        Assert.Contains(result.Violations, violation => violation.Gate == "rule");
    }

    [Fact]
    public void ResponseBlockedByTier1IsAGateFailureNotASilentPass()
    {
        GoldenCase good = Case("good");
        Tier2Result result = Tier2Gate.Apply(
            Run([good], Record(good, response: string.Empty, blocked: true)),
            [good],
            [],
            Thresholds);

        Assert.Contains(result.Violations, violation => violation.Gate == "blocked");
    }

    [Fact]
    public void UnscoredMetricIsSurfacedRatherThanTreatedAsAPass()
    {
        GoldenCase good = Case("good");
        TriadResult unscored = new("good",
            [new TriadScore(TriadMetrics.Relevance, null, TriadVerdict.NotScored, "judge failed")]);

        Tier2Result result = Tier2Gate.Apply(Run([good], Record(good)), [good], [unscored], Thresholds);

        Assert.True(result.Passed);
        Assert.Contains(result.Warnings, warning => warning.Contains("not scored", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(2.9, TriadVerdict.Fail)]
    [InlineData(3.0, TriadVerdict.Warn)]
    [InlineData(3.9, TriadVerdict.Warn)]
    [InlineData(4.0, TriadVerdict.Pass)]
    public void BandBoundariesAreInclusiveAtTheBottom(double score, TriadVerdict expected)
    {
        Assert.Equal(expected, new ThresholdBand(3.0, 4.0).Classify(score));
    }
}
