using System.Text.Json.Serialization;
using EvalFramework.Datasets;
using EvalFramework.Execution;
using EvalFramework.RagTriad;
using EvalFramework.Rules;

namespace EvalFramework.Incident;

/// <summary>The verdict on a replayed incident.</summary>
public sealed record IncidentReport
{
    [JsonPropertyName("incidentId")]
    public required string IncidentId { get; init; }

    [JsonPropertyName("relatedCaseId")]
    public string? RelatedCaseId { get; init; }

    /// <summary>Rules the production response broke. Empty means the rules did not catch it.</summary>
    [JsonPropertyName("ruleFailures")]
    public required IReadOnlyList<CheckResult> RuleFailures { get; init; }

    /// <summary>Tool calls that today's rules would have rejected.</summary>
    [JsonPropertyName("toolFailures")]
    public required IReadOnlyList<string> ToolFailures { get; init; }

    [JsonPropertyName("missingChunks")]
    public required IReadOnlyList<string> MissingChunks { get; init; }

    [JsonPropertyName("triad")]
    public TriadResult? Triad { get; init; }

    /// <summary>
    /// True when nothing deterministic explains the incident, which usually means the golden set
    /// needs a new case rather than the agent needing a fix.
    /// </summary>
    [JsonIgnore]
    public bool UnexplainedByRules =>
        RuleFailures.Count == 0 && ToolFailures.Count == 0 && MissingChunks.Count == 0;
}

/// <summary>
/// Replays a captured incident against today's rules.
/// </summary>
/// <remarks>
/// The most valuable outcome is a rule failure, because it proves the guard now catches what
/// production missed. The second most valuable is no failure at all: that is the signal to add a
/// golden case, since neither Tier 1 nor Tier 2 would catch a recurrence.
/// </remarks>
public static class IncidentReplay
{
    public static IncidentReport Replay(
        IncidentTrace trace,
        GoldenCase? relatedCase,
        IReadOnlyList<ToolArgumentRule> toolRules,
        ResponseRuleSet? fallbackRules = null,
        TriadResult? triad = null)
    {
        ArgumentNullException.ThrowIfNull(trace);

        ResponseRuleSet rules = relatedCase?.ToRuleSet()
            ?? fallbackRules
            ?? new ResponseRuleSet();

        RuleReport report = ResponseRules.Evaluate(rules, trace.Response);

        Dictionary<string, ToolArgumentRule> byName =
            toolRules.ToDictionary(rule => rule.ToolName, StringComparer.OrdinalIgnoreCase);

        List<string> toolFailures = [];

        foreach (ToolCallRecord call in trace.ToolCalls)
        {
            if (!byName.TryGetValue(call.Name, out ToolArgumentRule? rule))
            {
                continue;
            }

            RuleReport toolReport = ToolArgumentRules.Evaluate(rule, call.Arguments);
            toolFailures.AddRange(toolReport.Failures.Select(f => $"{call.Name}: {f.Name} ({f.Detail})"));
        }

        HashSet<string> retrieved = new(
            trace.Retrieval?.Chunks.Select(chunk => chunk.Id) ?? [],
            StringComparer.OrdinalIgnoreCase);

        string[] missing = (relatedCase?.ExpectedChunkIds ?? [])
            .Where(id => !retrieved.Contains(id))
            .ToArray();

        return new IncidentReport
        {
            IncidentId = trace.IncidentId,
            RelatedCaseId = trace.RelatedCaseId ?? relatedCase?.Id,
            RuleFailures = report.Failures,
            ToolFailures = toolFailures,
            MissingChunks = missing,
            Triad = triad
        };
    }

    /// <summary>Converts a trace into a response record so the triad can score it unchanged.</summary>
    public static ResponseRecord ToResponseRecord(IncidentTrace trace, RuleReport rules) => new()
    {
        CaseId = trace.RelatedCaseId ?? trace.IncidentId,
        Query = trace.Query,
        Repetition = 1,
        Response = trace.Response,
        LatencyMs = 0,
        Rules = rules,
        Retrieval = trace.Retrieval
    };
}

