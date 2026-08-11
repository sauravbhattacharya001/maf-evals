using EvalFramework.Datasets;
using EvalFramework.Execution;
using EvalFramework.RagTriad;
using EvalFramework.Rules;
using EvalFramework.Statistics;

namespace EvalFramework.Tests;

/// <summary>
/// An eval framework that miscounts infrastructure failures reports an outage as a regression.
/// These tests pin the distinction between missing data and a measurement.
/// </summary>
public sealed class ErrorHandlingTests
{
    private static readonly EvalConfig Config = new()
    {
        MinOverallPassRate = 0.0,
        MinCriticalCasePassRate = 0.0,
        MinStandardCasePassRate = 0.0
    };

    private static readonly GoldenCase Case = new()
    {
        Id = "case",
        Query = "q",
        MinLength = 1,
        RequireActionableFormat = false
    };

    private static ResponseRecord Record(ResponseOutcome outcome, string response, int repetition = 1) => new()
    {
        CaseId = Case.Id,
        Query = Case.Query,
        Repetition = repetition,
        Response = response,
        LatencyMs = 10,
        Rules = ResponseRules.Evaluate(Case.ToRuleSet(), response),
        Outcome = outcome,
        Error = outcome == ResponseOutcome.Errored ? "429 Too Many Requests" : null
    };

    private static RunArtifact Run(params ResponseRecord[] records) =>
        AgentRunner.Build([Case], records, records.Length, "tier3", "m", "d.jsonl");

    [Fact]
    public void ErroredInvocationsAreExcludedFromThePassRate()
    {
        // Two good responses and one rate limit is 100%, not 67%.
        RunArtifact run = Run(
            Record(ResponseOutcome.Completed, "a good answer", 1),
            Record(ResponseOutcome.Completed, "a good answer", 2),
            Record(ResponseOutcome.Errored, string.Empty, 3));

        Assert.Equal(1.0, run.OverallPassRate, 6);
        Assert.Equal(1, run.ErroredCount);
    }

    [Fact]
    public void ErroredInvocationsDoNotShrinkTheConfidenceIntervalDishonestly()
    {
        RunArtifact run = Run(
            Record(ResponseOutcome.Completed, "a good answer", 1),
            Record(ResponseOutcome.Errored, string.Empty, 2));

        // One usable observation, so the interval must stay wide.
        Assert.True(run.OverallLowerBound < 0.5);
    }

    [Fact]
    public void BlockedResponsesStillCountAsFailuresBecauseTheyAreRealOutcomes()
    {
        RunArtifact run = Run(
            Record(ResponseOutcome.Completed, "a good answer", 1),
            Record(ResponseOutcome.Blocked, string.Empty, 2));

        Assert.Equal(0.5, run.OverallPassRate, 6);
        Assert.Equal(0, run.ErroredCount);
    }

    [Fact]
    public void AnyErrorFailsTheTier3GateByDefault()
    {
        RunArtifact run = Run(
            Record(ResponseOutcome.Completed, "a good answer", 1),
            Record(ResponseOutcome.Errored, string.Empty, 2));

        GateReport gates = RunAnalyzer.ApplyGates(run, Config);

        Assert.False(gates.Passed);
        Assert.Contains(gates.Violations, violation => violation.Gate == "infrastructure");
    }

    [Fact]
    public void ATolerantConfigCanAcceptSomeErrors()
    {
        RunArtifact run = Run(
            Record(ResponseOutcome.Completed, "a good answer", 1),
            Record(ResponseOutcome.Errored, string.Empty, 2));

        GateReport gates = RunAnalyzer.ApplyGates(run, Config with { MaxErrorRate = 0.5 });

        Assert.DoesNotContain(gates.Violations, violation => violation.Gate == "infrastructure");
    }

    [Fact]
    public void Tier2RefusesToPassOnIncompleteEvidence()
    {
        RunArtifact run = Run(Record(ResponseOutcome.Errored, string.Empty));

        Tier2Result result = Tier2Gate.Apply(run, [Case], [], new TriadThresholds());

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, violation => violation.Gate == "infrastructure");
    }

    [Fact]
    public void ErroredRecordsCarryTheirCauseForDiagnosis()
    {
        ResponseRecord record = Record(ResponseOutcome.Errored, string.Empty);

        Assert.True(record.Errored);
        Assert.False(record.Counts);
        Assert.Contains("429", record.Error!, StringComparison.Ordinal);
    }
}
