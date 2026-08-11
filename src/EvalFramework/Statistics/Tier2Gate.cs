using System.Text.Json.Serialization;
using EvalFramework.Cost;
using EvalFramework.Datasets;
using EvalFramework.Execution;
using EvalFramework.RagTriad;

namespace EvalFramework.Statistics;

/// <summary>A Tier 2 run: what the agent did, what the judge thought, and whether it may merge.</summary>
public sealed record Tier2Result
{
    public const string CurrentSchemaVersion = "tier2/v1";

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("run")]
    public required RunArtifact Run { get; init; }

    [JsonPropertyName("triad")]
    public required IReadOnlyList<TriadResult> Triad { get; init; }

    [JsonPropertyName("violations")]
    public required IReadOnlyList<GateViolation> Violations { get; init; }

    [JsonPropertyName("warnings")]
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>
    /// False when the triad was skipped, so a cheap run cannot be mistaken for a full gate.
    /// </summary>
    /// <remarks>
    /// A partial gate that prints an unqualified "PASSED" is how an unverified change reaches
    /// main. The result records what was actually checked, not merely whether it complained.
    /// </remarks>
    [JsonPropertyName("triadEvaluated")]
    public required bool TriadEvaluated { get; init; }

    /// <summary>Judge model spend, tracked separately because it is billed separately.</summary>
    [JsonPropertyName("judgeUsage")]
    public CostSummary? JudgeUsage { get; init; }

    /// <summary>Thresholds in force for this run, so a report can say what was advisory.</summary>
    [JsonPropertyName("thresholds")]
    public TriadThresholds Thresholds { get; init; } = new();

    [JsonIgnore]
    public bool Passed => Violations.Count == 0;

    /// <summary>A gate that skipped its judge is a smoke test, not a verdict.</summary>
    [JsonIgnore]
    public string Verdict => (Passed, TriadEvaluated) switch
    {
        (false, _) => "FAILED",
        (true, true) => "PASSED",
        (true, false) => "PASSED (PARTIAL: triad skipped)"
    };
}

/// <summary>
/// The pull-request gate: deterministic rules, retrieval expectations, then the triad.
/// </summary>
/// <remarks>
/// Rules and retrieval expectations are exact and always block. Triad metrics use bands, so only a
/// score below the floor blocks; a merely mediocre score is surfaced as a warning. This keeps a
/// stochastic judge from making the gate flaky while still failing on genuine quality collapses.
/// </remarks>
public static class Tier2Gate
{
    public static Tier2Result Apply(
        RunArtifact run,
        IReadOnlyList<GoldenCase> cases,
        IReadOnlyList<TriadResult> triad,
        TriadThresholds thresholds,
        bool triadEvaluated = true,
        CostSummary? judgeUsage = null,
        double? maxRunCostUsd = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(triad);

        Dictionary<string, GoldenCase> lookup = cases.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        List<GateViolation> violations = [];
        List<string> warnings = [];

        foreach (ResponseRecord record in run.Responses)
        {
            if (record.Errored)
            {
                violations.Add(new GateViolation(
                    "infrastructure",
                    $"{record.CaseId}: invocation errored ({record.Error}). " +
                    "The gate cannot pass on incomplete evidence."));
                continue;
            }

            if (record.Blocked)
            {
                violations.Add(new GateViolation("blocked", $"{record.CaseId}: Tier 1 blocked the response"));
                continue;
            }

            foreach (Rules.CheckResult failure in record.Rules.Failures)
            {
                violations.Add(new GateViolation("rule", $"{record.CaseId}: {failure.Name} ({failure.Detail})"));
            }

            if (lookup.TryGetValue(record.CaseId, out GoldenCase? goldenCase))
            {
                violations.AddRange(CheckRetrievalExpectations(record, goldenCase));

                ToolCallCheck.Result tools = ToolCallCheck.Evaluate(goldenCase, record.ToolCalls);

                foreach (string problem in tools.Problems)
                {
                    violations.Add(new GateViolation("tool_call", $"{record.CaseId}: {problem}"));
                }
            }
        }

        foreach (TriadResult result in triad)
        {
            foreach (TriadScore score in result.Scores)
            {
                ThresholdBand band = thresholds.For(score.Metric);

                switch (score.Verdict)
                {
                    // An advisory metric is reported but never blocks, because it has been measured
                    // to disagree with itself across repeated judging.
                    case TriadVerdict.Fail when !band.Blocking:
                        warnings.Add(
                            $"{result.CaseId}: {score.Metric} scored {score.Score:F1}, " +
                            $"below floor {band.Floor:F1} (advisory)");
                        break;

                    case TriadVerdict.Fail:
                        violations.Add(new GateViolation(
                            "triad",
                            $"{result.CaseId}: {score.Metric} scored {score.Score:F1}, " +
                            $"below floor {band.Floor:F1}"));
                        break;

                    case TriadVerdict.Warn:
                        warnings.Add(
                            $"{result.CaseId}: {score.Metric} scored {score.Score:F1}, " +
                            $"below target {band.Target:F1}");
                        break;

                    case TriadVerdict.NotScored:
                        warnings.Add($"{result.CaseId}: {score.Metric} was not scored by the judge");
                        break;
                }
            }
        }

        double? totalCost = ModelPricing.Total(run.Usage, judgeUsage);

        if (maxRunCostUsd is double budget)
        {
            if (totalCost is double spent && spent > budget)
            {
                violations.Add(new GateViolation(
                    "budget",
                    $"run cost ${spent:F4} exceeds budget ${budget:F4}"));
            }
            else if (totalCost is null)
            {
                // A budget cannot be enforced against an unpriced model, and silently passing
                // would make the gate look stricter than it is.
                warnings.Add("budget not enforced: no price configured for one or more models");
            }
        }

        return new Tier2Result
        {
            Run = run,
            Triad = triad,
            Violations = violations,
            Warnings = warnings,
            TriadEvaluated = triadEvaluated,
            JudgeUsage = judgeUsage,
            Thresholds = thresholds
        };
    }

    /// <summary>
    /// A deterministic retrieval check that needs no judge: did the expected chunks come back?
    /// </summary>
    private static IEnumerable<GateViolation> CheckRetrievalExpectations(
        ResponseRecord record,
        GoldenCase goldenCase)
    {
        if (goldenCase.ExpectedChunkIds.Count == 0)
        {
            yield break;
        }

        HashSet<string> retrieved = new(
            record.Retrieval?.Chunks.Select(chunk => chunk.Id) ?? [],
            StringComparer.OrdinalIgnoreCase);

        string[] missing = goldenCase.ExpectedChunkIds.Where(id => !retrieved.Contains(id)).ToArray();

        if (missing.Length > 0)
        {
            yield return new GateViolation(
                "retrieval",
                $"{record.CaseId}: expected chunk(s) not retrieved: {string.Join(", ", missing)}");
        }
    }
}


