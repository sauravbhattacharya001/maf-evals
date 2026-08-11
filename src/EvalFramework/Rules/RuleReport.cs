using System.Text.Json.Serialization;

namespace EvalFramework.Rules;

/// <summary>Outcome of a single rule.</summary>
public sealed record CheckResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("severity")] RuleSeverity Severity);

/// <summary>
/// The combined result of running a rule set. Produced identically in the hot path
/// (Tier 1), in CI (Tier 2), and during analysis (Tier 3).
/// </summary>
public sealed record RuleReport(
    [property: JsonPropertyName("checks")] IReadOnlyList<CheckResult> Checks)
{
    [JsonPropertyName("passed")]
    public bool Passed => Checks.All(check => check.Passed);

    [JsonIgnore]
    public IReadOnlyList<CheckResult> Failures => Checks.Where(check => !check.Passed).ToArray();

    /// <summary>Highest severity among failures, or <see langword="null"/> when everything passed.</summary>
    [JsonIgnore]
    public RuleSeverity? HighestSeverity =>
        Failures.Count == 0 ? null : Failures.Max(failure => failure.Severity);

    /// <summary>True when at least one failure justifies spending another model call.</summary>
    [JsonIgnore]
    public bool ShouldRetry => HighestSeverity >= RuleSeverity.Retry;

    /// <summary>True when a failure must never be returned to the caller.</summary>
    [JsonIgnore]
    public bool ShouldBlock => HighestSeverity == RuleSeverity.Block;

    /// <summary>Feedback appended to a retry so the model is told what to fix.</summary>
    public string ToCorrectionMessage()
    {
        IEnumerable<string> problems = Failures
            .Where(failure => failure.Severity >= RuleSeverity.Retry)
            .Select(failure => $"- {failure.Name}: {failure.Detail}");

        return "Your previous answer did not meet these requirements:\n"
            + string.Join("\n", problems)
            + "\nRewrite the answer so it satisfies them.";
    }
}
