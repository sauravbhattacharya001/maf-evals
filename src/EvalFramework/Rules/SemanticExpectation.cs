using System.Text.Json.Serialization;

namespace EvalFramework.Rules;

/// <summary>
/// A meaning-based expectation, checked by embedding similarity rather than by matching words.
/// </summary>
/// <remarks>
/// <para>
/// Written after a keyword list failed three times in a row on correct behaviour. The agent declined
/// an over-limit refund and explained the cap, phrasing it as "without escalation", then "up to 500
/// units", then "without additional approval". Each fix added the missing synonym and the next run
/// found another. A list of words tests phrasing; the property being asserted is meaning.
/// </para>
/// <para>
/// Embeddings rather than a judge model, deliberately. A judge was measured flipping 17% of verdicts
/// on identical input, which is intolerable in a gate. Embeddings are deterministic for a fixed
/// model, roughly a thousand times cheaper, and compare meaning without inventing an opinion.
/// </para>
/// <para>
/// This cannot run in Tier 1: the hot path must stay free of network calls. Semantic expectations are
/// evaluated in Tier 2, alongside the other checks that already need a model.
/// </para>
/// </remarks>
public sealed record SemanticExpectation
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Reference statements. The response must resemble at least one of them.</summary>
    [JsonPropertyName("anyOf")]
    public required IReadOnlyList<string> AnyOf { get; init; }

    /// <summary>
    /// Cosine similarity required, 0 to 1. Calibrate against real responses rather than guessing:
    /// too high rejects correct paraphrases, too low accepts anything on the topic.
    /// </summary>
    [JsonPropertyName("minSimilarity")]
    public double MinSimilarity { get; init; } = 0.55;

    [JsonPropertyName("severity")]
    public RuleSeverity Severity { get; init; } = RuleSeverity.Retry;
}
