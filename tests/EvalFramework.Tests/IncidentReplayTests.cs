using EvalFramework.Datasets;
using EvalFramework.Incident;
using EvalFramework.Rules;
using SupportAgent;

namespace EvalFramework.Tests;

/// <summary>
/// Incident replay is the one Tier 3 path that runs fully offline, because a captured trace
/// contains everything needed to re-apply the deterministic rules.
/// </summary>
public sealed class IncidentReplayTests
{
    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "incidents")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("incidents directory not found.");
    }

    private static IncidentTrace Sample() =>
        IncidentTrace.Load(Path.Combine(RepoRoot(), "incidents", "sample-incident.json"));

    private static GoldenCase RelatedCase()
    {
        IReadOnlyList<GoldenCase> cases = GoldenSet.Load(
            Path.Combine(RepoRoot(), "datasets", "support-golden-set.jsonl"));

        return cases.Single(item => item.Id == "medical-advice-refusal");
    }

    [Fact]
    public void SampleTraceLoads()
    {
        IncidentTrace trace = Sample();

        Assert.Equal(IncidentTrace.CurrentSchemaVersion, trace.SchemaVersion);
        Assert.Equal("medical-advice-refusal", trace.RelatedCaseId);
        Assert.Single(trace.ToolCalls);
    }

    [Fact]
    public void TodaysRulesCatchTheUnsafeAdviceThatShipped()
    {
        IncidentReport report = IncidentReplay.Replay(Sample(), RelatedCase(), SupportPolicy.ToolRules);

        Assert.Contains(report.RuleFailures, failure => failure.Name == RuleNames.ForbiddenTerms);
        Assert.False(report.UnexplainedByRules);
    }

    [Fact]
    public void TodaysToolGuardWouldHaveBlockedTheOversizedRefund()
    {
        IncidentReport report = IncidentReplay.Replay(Sample(), RelatedCase(), SupportPolicy.ToolRules);

        Assert.Contains(report.ToolFailures, failure => failure.Contains("amount", StringComparison.Ordinal));
        Assert.Contains(report.ToolFailures, failure => failure.Contains("reason", StringComparison.Ordinal));
    }

    [Fact]
    public void RetrievalGapIsIdentified()
    {
        IncidentReport report = IncidentReplay.Replay(Sample(), RelatedCase(), SupportPolicy.ToolRules);

        Assert.Contains("safety#2", report.MissingChunks);
    }

    [Fact]
    public void CleanTraceIsReportedAsUnexplainedSoAGoldenCaseGetsAdded()
    {
        IncidentTrace clean = new()
        {
            IncidentId = "INC-CLEAN",
            Query = "How do I cancel?",
            Response = "1. Open Account.\n2. Choose Subscriptions and cancel at least 24 hours before renewal."
        };

        IncidentReport report = IncidentReplay.Replay(
            clean,
            relatedCase: null,
            SupportPolicy.ToolRules,
            fallbackRules: SupportPolicy.BaselineRules);

        Assert.True(report.UnexplainedByRules);
    }
}
