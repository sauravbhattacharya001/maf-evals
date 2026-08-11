using System.Text.Json;
using EvalFramework.Calibration;
using EvalFramework.Execution;
using EvalFramework.Incident;
using EvalFramework.RagTriad;
using EvalFramework.Rules;
using EvalFramework.Statistics;

namespace EvalFramework.Tests;

/// <summary>
/// Artifacts are the contract between tiers and across time.
/// </summary>
/// <remarks>
/// Tier 3 reads what Tier 2 wrote, incidents are replayed months later, and baselines are compared
/// against runs recorded by an older build. Declaring a schemaVersion promises those files remain
/// readable; nothing enforced that promise until these tests. The committed fixtures are the
/// enforcement: if a change breaks them, the version must be bumped and a migration considered.
/// </remarks>
public sealed class SchemaCompatibilityTests
{
    private static string FixturePath(string name)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "testdata")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory!.FullName, "testdata", "schemas", name);
    }

    private static T RoundTrip<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonDefaults.Options), JsonDefaults.Options)!;

    [Fact]
    public void CommittedRunFixtureStillParses()
    {
        RunArtifact run = ArtifactReader.ReadRun(FixturePath("run-v3.json"));

        Assert.Equal("run/v3", run.SchemaVersion);
        Assert.Equal(2, run.Responses.Count);
        Assert.Equal(1, run.ErroredCount);
    }

    [Fact]
    public void CommittedRunFixturePreservesOutcomeSemantics()
    {
        RunArtifact run = ArtifactReader.ReadRun(FixturePath("run-v3.json"));

        ResponseRecord completed = run.Responses[0];
        ResponseRecord errored = run.Responses[1];

        Assert.Equal(ResponseOutcome.Completed, completed.Outcome);
        Assert.True(completed.Counts);
        Assert.Equal(2, completed.Attempts);
        Assert.Equal("issue_refund", completed.RejectedToolCalls[0]);

        Assert.True(errored.Errored);
        Assert.False(errored.Counts);
        Assert.Contains("429", errored.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void CommittedTier2FixtureStillParses()
    {
        Tier2Result result = ArtifactReader.ReadTier2(FixturePath("tier2-v1.json"));

        Assert.True(result.Passed);
        Assert.True(result.TriadEvaluated);
        Assert.False(result.Thresholds.Retrieval.Blocking);
        Assert.Equal(3.5, result.Thresholds.Groundedness.Floor);
    }

    [Fact]
    public void CommittedTier2FixturePreservesUnscoredMetrics()
    {
        Tier2Result result = ArtifactReader.ReadTier2(FixturePath("tier2-v1.json"));
        TriadScore relevance = result.Triad[0].Scores.Single(s => s.Metric == TriadMetrics.Relevance);

        Assert.Null(relevance.Score);
        Assert.Equal(TriadVerdict.NotScored, relevance.Verdict);
    }

    [Fact]
    public void CommittedIncidentFixtureStillParses()
    {
        IncidentTrace trace = IncidentTrace.Load(FixturePath("incident-v1.json"));

        Assert.Equal("INC-FIXTURE", trace.IncidentId);
        Assert.Single(trace.ToolCalls);
        Assert.Equal("safety#2", trace.Retrieval!.Chunks[0].Id);
    }

    [Fact]
    public void ToolCallArgumentsSurviveAsUsableValues()
    {
        // Arguments arrive as JsonElement after a round trip; the guard must still read them.
        IncidentTrace trace = IncidentTrace.Load(FixturePath("incident-v1.json"));

        RuleReport report = ToolArgumentRules.Evaluate(
            new ToolArgumentRule
            {
                ToolName = "issue_refund",
                NumericRanges = new Dictionary<string, NumericRange> { ["amount"] = new(0, 500) }
            },
            trace.ToolCalls[0].Arguments);

        Assert.False(report.Passed);
    }

    [Fact]
    public void EnumsAreWrittenAsNamesSoDiffsStayReadable()
    {
        string json = JsonSerializer.Serialize(
            new CheckResult("min_length", false, "too short", RuleSeverity.Block),
            JsonDefaults.Options);

        Assert.Contains("\"Block\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"severity\": 2", json, StringComparison.Ordinal);
    }

    [Fact]
    public void RunArtifactSurvivesARoundTrip()
    {
        RunArtifact original = ArtifactReader.ReadRun(FixturePath("run-v3.json"));
        RunArtifact copy = RoundTrip(original);

        Assert.Equal(original.RunId, copy.RunId);
        Assert.Equal(original.ErroredCount, copy.ErroredCount);
        Assert.Equal(original.OverallPassRate, copy.OverallPassRate);
        Assert.Equal(original.Responses.Count, copy.Responses.Count);
        Assert.Equal(original.Responses[1].Outcome, copy.Responses[1].Outcome);
    }

    [Fact]
    public void Tier2ResultSurvivesARoundTrip()
    {
        Tier2Result original = ArtifactReader.ReadTier2(FixturePath("tier2-v1.json"));
        Tier2Result copy = RoundTrip(original);

        Assert.Equal(original.Verdict, copy.Verdict);
        Assert.Equal(original.Thresholds.Groundedness.Floor, copy.Thresholds.Groundedness.Floor);
        Assert.Equal(original.Triad[0].Scores.Count, copy.Triad[0].Scores.Count);
    }

    [Fact]
    public void CalibrationReportSurvivesARoundTrip()
    {
        CalibrationReport original = new()
        {
            TimestampUtc = DateTimeOffset.UnixEpoch,
            JudgeModel = "fixture-judge",
            Agreement =
            [
                AgreementMetrics.Compare(
                    TriadMetrics.Relevance,
                    [new ScorePair("c", TriadMetrics.Relevance, 5, 4)],
                    new ThresholdBand(3, 4))
            ],
            Consistency = ConsistencyMetrics.Summarize(
                [new ScorePair("c", TriadMetrics.Relevance, 5, 4, 1),
                 new ScorePair("c", TriadMetrics.Relevance, 5, 2, 2)],
                new TriadThresholds()),
            Pairs = [new ScorePair("c", TriadMetrics.Relevance, 5, 4)]
        };

        CalibrationReport copy = RoundTrip(original);

        Assert.Equal(CalibrationReport.CurrentSchemaVersion, copy.SchemaVersion);
        Assert.Equal(original.Agreement[0].MeanAbsoluteError, copy.Agreement[0].MeanAbsoluteError);
        Assert.Equal(
            original.Consistency.Single(s => s.Metric == TriadMetrics.Relevance).VerdictFlipRate,
            copy.Consistency.Single(s => s.Metric == TriadMetrics.Relevance).VerdictFlipRate);
    }

    [Fact]
    public void CurrentSchemaVersionsMatchTheCommittedFixtures()
    {
        // If someone bumps a version without adding a fixture, this fails and asks for one.
        Assert.Equal(RunArtifact.CurrentSchemaVersion, ArtifactReader.SchemaOf(FixturePath("run-v3.json")));
        Assert.Equal(Tier2Result.CurrentSchemaVersion, ArtifactReader.SchemaOf(FixturePath("tier2-v1.json")));
        Assert.Equal(IncidentTrace.CurrentSchemaVersion, IncidentTrace.Load(FixturePath("incident-v1.json")).SchemaVersion);
    }
}
