using System.Text.Json.Serialization;
using EvalFramework.Rules;

namespace EvalFramework.Datasets;

/// <summary>
/// One versioned golden-set case. Deterministic rules live with the case so that
/// adding coverage is a data change, not a code change.
/// </summary>
public sealed class GoldenCase
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("query")]
    public required string Query { get; init; }

    /// <summary>Critical cases are held to a stricter Tier 2 gate.</summary>
    [JsonPropertyName("critical")]
    public bool Critical { get; init; }

    /// <summary>Terms that must all appear in the response (case-insensitive).</summary>
    [JsonPropertyName("expectedTerms")]
    public IReadOnlyList<string> ExpectedTerms { get; init; } = [];

    /// <summary>Terms that must never appear, such as refusal or unsafe phrasing.</summary>
    [JsonPropertyName("forbiddenTerms")]
    public IReadOnlyList<string> ForbiddenTerms { get; init; } = [];

    [JsonPropertyName("minLength")]
    public int MinLength { get; init; } = 40;

    /// <summary>Requires a numbered or bulleted list, used for "give me steps" cases.</summary>
    [JsonPropertyName("requireActionableFormat")]
    public bool RequireActionableFormat { get; init; } = true;

    /// <summary>
    /// Corpus chunk ids retrieval must return for this case. Checked deterministically, so a
    /// retrieval regression is caught without spending a judge call.
    /// </summary>
    [JsonPropertyName("expectedChunkIds")]
    public IReadOnlyList<string> ExpectedChunkIds { get; init; } = [];

    /// <summary>
    /// Optional per-rule severity overrides. Only Tier 1 honours these; Tier 2 gates on
    /// every rule regardless.
    /// </summary>
    [JsonPropertyName("severities")]
    public IReadOnlyDictionary<string, RuleSeverity> Severities { get; init; } =
        new Dictionary<string, RuleSeverity>();

    public ResponseRuleSet ToRuleSet() => new()
    {
        MinLength = MinLength,
        ExpectedTerms = ExpectedTerms,
        ForbiddenTerms = ForbiddenTerms,
        RequireActionableFormat = RequireActionableFormat,
        Severities = Severities
    };
}
