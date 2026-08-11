using System.Text.Json.Serialization;
using EvalFramework.Datasets;
using EvalFramework.Execution;
using EvalFramework.RagTriad;

namespace EvalFramework.Statistics;

/// <summary>A Tier 2 run: what the agent did, what the judge thought, and whether it may merge.</summary>
public sealed record Tier2Result
{
    [JsonPropertyName("run")]
    public required RunArtifact Run { get; init; }

    [JsonPropertyName("triad")]
    public required IReadOnlyList<TriadResult> Triad { get; init; }

    [JsonPropertyName("violations")]
    public required IReadOnlyList<GateViolation> Violations { get; init; }

    [JsonPropertyName("warnings")]
    public required IReadOnlyList<string> Warnings { get; init; }

    [JsonIgnore]
    public bool Passed => Violations.Count == 0;
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
        TriadThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(triad);

        Dictionary<string, GoldenCase> lookup = cases.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        List<GateViolation> violations = [];
        List<string> warnings = [];

        foreach (ResponseRecord record in run.Responses)
        {
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
            }
        }

        foreach (TriadResult result in triad)
        {
            foreach (TriadScore score in result.Scores)
            {
                switch (score.Verdict)
                {
                    case TriadVerdict.Fail:
                        violations.Add(new GateViolation(
                            "triad",
                            $"{result.CaseId}: {score.Metric} scored {score.Score:F1}, " +
                            $"below floor {thresholds.For(score.Metric).Floor:F1}"));
                        break;

                    case TriadVerdict.Warn:
                        warnings.Add(
                            $"{result.CaseId}: {score.Metric} scored {score.Score:F1}, " +
                            $"below target {thresholds.For(score.Metric).Target:F1}");
                        break;

                    case TriadVerdict.NotScored:
                        warnings.Add($"{result.CaseId}: {score.Metric} was not scored by the judge");
                        break;
                }
            }
        }

        return new Tier2Result
        {
            Run = run,
            Triad = triad,
            Violations = violations,
            Warnings = warnings
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
