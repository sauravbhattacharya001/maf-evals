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

    /// <summary>
    /// The customer turns, for a conversation that takes more than one message.
    /// </summary>
    /// <remarks>
    /// A single question rarely resembles real support traffic. Faults that only appear later in a
    /// conversation are invisible to a one-shot case: the agent forgets an order number it was
    /// already given, contradicts an earlier answer, or repeats a question the customer answered.
    /// When this list is empty the case is a single turn built from <see cref="Query"/>.
    /// </remarks>
    [JsonPropertyName("turns")]
    public IReadOnlyList<string> Turns { get; init; } = [];

    /// <summary>The turns to send, whether the case declares one or several.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> EffectiveTurns => Turns.Count > 0 ? Turns : [Query];

    [JsonIgnore]
    public bool IsMultiTurn => Turns.Count > 1;

    /// <summary>What this case is probing, for adversarial sets. Documentation, not a rule.</summary>
    [JsonPropertyName("attack")]
    public string? Attack { get; init; }

    /// <summary>Critical cases are held to a stricter Tier 2 gate.</summary>
    [JsonPropertyName("critical")]
    public bool Critical { get; init; }

    /// <summary>Terms that must all appear in the response (case-insensitive).</summary>
    [JsonPropertyName("expectedTerms")]
    public IReadOnlyList<string> ExpectedTerms { get; init; } = [];

    /// <summary>Groups of alternatives; at least one term from each group must appear.</summary>
    [JsonPropertyName("expectedAnyTerms")]
    public IReadOnlyList<IReadOnlyList<string>> ExpectedAnyTerms { get; init; } = [];

    /// <summary>Terms that must never appear, such as refusal or unsafe phrasing.</summary>
    [JsonPropertyName("forbiddenTerms")]
    public IReadOnlyList<string> ForbiddenTerms { get; init; } = [];

    [JsonPropertyName("minLength")]
    public int MinLength { get; init; } = 40;

    /// <summary>Requires a numbered or bulleted list, used for "give me steps" cases.</summary>
    [JsonPropertyName("requireActionableFormat")]
    public bool RequireActionableFormat { get; init; } = true;

    /// <summary>
    /// Tools the agent must call, with optional argument expectations. Checked deterministically:
    /// choosing the right tool is a fact, not a judgement, so it needs no model to verify.
    /// </summary>
    [JsonPropertyName("expectedToolCalls")]
    public IReadOnlyList<ExpectedToolCall> ExpectedToolCalls { get; init; } = [];

    /// <summary>
    /// Meaning-based expectations, checked by embedding similarity in Tier 2. Use these where a
    /// property is about what was said rather than what was done, since a word list tests phrasing.
    /// </summary>
    [JsonPropertyName("semanticExpectations")]
    public IReadOnlyList<SemanticExpectation> SemanticExpectations { get; init; } = [];

    /// <summary>
    /// Tools the agent must not successfully call. A call the guard rejected does not count as a
    /// violation, which keeps "escalated correctly" distinct from "tried and was stopped".
    /// </summary>
    [JsonPropertyName("forbiddenToolCalls")]
    public IReadOnlyList<string> ForbiddenToolCalls { get; init; } = [];

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
        ExpectedAnyTerms = ExpectedAnyTerms,
        ForbiddenTerms = ForbiddenTerms,
        RequireActionableFormat = RequireActionableFormat,
        Severities = Severities
    };
}





