using EvalFramework.Datasets;
using EvalFramework.Deterministic;
using EvalFramework.Execution;
using EvalFramework.Statistics;

namespace EvalFramework.Tests;

public sealed class RunAnalyzerTests
{
    private static readonly EvalConfig Config = new()
    {
        Repetitions = 4,
        MinOverallPassRate = 0.50,
        MinCriticalCasePassRate = 1.00,
        MinStandardCasePassRate = 0.50
    };

    private static readonly GoldenCase Critical = new()
    {
        Id = "critical-case",
        Query = "q1",
        Critical = true,
        RequireActionableFormat = false,
        MinLength = 1
    };

    private static readonly GoldenCase Standard = new()
    {
        Id = "standard-case",
        Query = "q2",
        RequireActionableFormat = false,
        MinLength = 1
    };

    private static ResponseRecord Record(GoldenCase source, int repetition, bool passed) => new()
    {
        CaseId = source.Id,
        Query = source.Query,
        Repetition = repetition,
        Response = passed ? "a sufficiently long response" : string.Empty,
        LatencyMs = 100,
        Deterministic = DeterministicEvaluator.Evaluate(source, passed ? "a sufficiently long response" : string.Empty)
    };

    private static RunArtifact Build(params ResponseRecord[] records) =>
        GoldenSetRunner.Build([Critical, Standard], records, Config, "test-model", "test.jsonl");

    [Fact]
    public void IntermittentFailureIsReportedAsFlaky()
    {
        RunArtifact run = Build(
            Record(Critical, 1, true),
            Record(Critical, 2, false),
            Record(Standard, 1, true),
            Record(Standard, 2, true));

        CaseStatistics critical = run.Cases.Single(item => item.CaseId == "critical-case");

        Assert.True(critical.Flaky);
        Assert.Equal(0.5, critical.PassRate, 6);
        Assert.False(run.Cases.Single(item => item.CaseId == "standard-case").Flaky);
    }

    [Fact]
    public void OneFailedCriticalRepetitionFailsTheRun()
    {
        RunArtifact run = Build(
            Record(Critical, 1, true),
            Record(Critical, 2, false),
            Record(Standard, 1, true),
            Record(Standard, 2, true));

        GateReport gates = RunAnalyzer.ApplyGates(run, Config);

        Assert.False(gates.Passed);
        Assert.Contains(gates.Violations, violation => violation.Gate == "critical_case");
    }

    [Fact]
    public void FullyPassingRunClearsEveryGate()
    {
        RunArtifact run = Build(
            Record(Critical, 1, true),
            Record(Critical, 2, true),
            Record(Standard, 1, true),
            Record(Standard, 2, true));

        Assert.True(RunAnalyzer.ApplyGates(run, Config).Passed);
    }

    [Fact]
    public void DropBelowBaselineIsFlaggedAsRegression()
    {
        RunArtifact run = Build(
            Record(Critical, 1, true),
            Record(Critical, 2, true),
            Record(Standard, 1, false),
            Record(Standard, 2, false));

        EvalConfig config = Config with
        {
            MinCriticalCasePassRate = 1.00,
            MinStandardCasePassRate = 0.00,
            MinOverallPassRate = 0.00,
            BaselineOverallPassRate = 1.00,
            MaxRegression = 0.05
        };

        GateReport gates = RunAnalyzer.ApplyGates(run, config);

        Assert.Contains(gates.Violations, violation => violation.Gate == "regression");
    }

    [Fact]
    public void LatencyBudgetIsEnforcedWhenConfigured()
    {
        RunArtifact run = Build(Record(Critical, 1, true), Record(Standard, 1, true));

        GateReport gates = RunAnalyzer.ApplyGates(run, Config with { MaxMeanLatencyMs = 50 });

        Assert.Contains(gates.Violations, violation => violation.Gate == "latency");
    }

    [Fact]
    public void FailureReasonsAreCountedForTriage()
    {
        RunArtifact run = Build(Record(Standard, 1, false), Record(Standard, 2, false));

        CaseStatistics statistics = run.Cases.Single(item => item.CaseId == "standard-case");

        Assert.Contains("min_length x2", statistics.TopFailures);
    }
}
