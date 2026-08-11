using System.Text.Json.Serialization;

namespace EvalFramework.Deterministic;

/// <summary>Outcome of a single deterministic rule.</summary>
public sealed record CheckResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("detail")] string Detail);

/// <summary>Deterministic score for one response of one case.</summary>
public sealed record DeterministicResult(
    [property: JsonPropertyName("caseId")] string CaseId,
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("checks")] IReadOnlyList<CheckResult> Checks)
{
    [JsonIgnore]
    public IEnumerable<CheckResult> Failures => Checks.Where(check => !check.Passed);
}
