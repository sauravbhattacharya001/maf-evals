using System.Text.Json.Serialization;

namespace EvalFramework.RagTriad;

/// <summary>
/// A pass band for a judge metric.
/// </summary>
/// <remarks>
/// Two thresholds rather than one. Judges are stochastic, so a single cut-off turns a borderline
/// score into a coin flip that blocks a pull request. Scores below <see cref="Floor"/> are real
/// failures; scores between <see cref="Floor"/> and <see cref="Target"/> are recorded as warnings
/// and reviewed rather than blocking a merge.
/// </remarks>
public readonly record struct ThresholdBand(double Floor, double Target, bool Blocking = true)
{
    public TriadVerdict Classify(double? score) => score switch
    {
        null => TriadVerdict.NotScored,
        double value when value < Floor => TriadVerdict.Fail,
        double value when value < Target => TriadVerdict.Warn,
        _ => TriadVerdict.Pass
    };
}

public enum TriadVerdict
{
    Pass,
    Warn,
    Fail,
    NotScored
}

/// <summary>One judge metric for one response.</summary>
public sealed record TriadScore(
    [property: JsonPropertyName("metric")] string Metric,
    [property: JsonPropertyName("score")] double? Score,
    [property: JsonPropertyName("verdict")] TriadVerdict Verdict,
    [property: JsonPropertyName("reason")] string? Reason);

/// <summary>The triad applied to one case.</summary>
public sealed record TriadResult(
    [property: JsonPropertyName("caseId")] string CaseId,
    [property: JsonPropertyName("scores")] IReadOnlyList<TriadScore> Scores)
{
    [JsonIgnore]
    public bool Failed => Scores.Any(score => score.Verdict == TriadVerdict.Fail);

    [JsonIgnore]
    public bool Warned => Scores.Any(score => score.Verdict == TriadVerdict.Warn);
}

/// <summary>Thresholds for the three triad metrics, on the evaluators' 1 to 5 scale.</summary>
public sealed record TriadThresholds
{
    /// <summary>
    /// Advisory, not blocking. Repeated judging of one identical input produced 5, 2, 4, 5, 2:
    /// a standard deviation of 1.36 and a verdict that flipped on every re-run. A metric that
    /// disagrees with itself cannot decide a merge. Retrieval quality is gated deterministically
    /// by <c>expectedChunkIds</c> instead, which is exact, stable, and free.
    /// </summary>
    [JsonPropertyName("retrieval")]
    public ThresholdBand Retrieval { get; init; } = new(3.0, 4.0, Blocking: false);

    /// <summary>
    /// Stable across repeats (standard deviation 0.00), so a threshold is meaningful here. The
    /// floor is 3.5 because calibration showed the judge scores outright fabrication at exactly
    /// 3.0; a floor of 3.0 let every hallucination case through as a warning.
    /// </summary>
    [JsonPropertyName("groundedness")]
    public ThresholdBand Groundedness { get; init; } = new(3.5, 4.0);

    [JsonPropertyName("relevance")]
    public ThresholdBand Relevance { get; init; } = new(3.0, 4.0);

    public ThresholdBand For(string metric) => metric switch
    {
        TriadMetrics.Retrieval => Retrieval,
        TriadMetrics.Groundedness => Groundedness,
        TriadMetrics.Relevance => Relevance,
        _ => new ThresholdBand(0, 0)
    };
}

public static class TriadMetrics
{
    public const string Retrieval = "Retrieval";
    public const string Groundedness = "Groundedness";
    public const string Relevance = "Relevance";
}
