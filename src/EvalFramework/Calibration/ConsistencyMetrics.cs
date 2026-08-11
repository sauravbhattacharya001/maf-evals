using System.Text.Json.Serialization;
using EvalFramework.RagTriad;

namespace EvalFramework.Calibration;

/// <summary>Spread of one metric's scores for one case across repeated judging.</summary>
public sealed record CaseConsistency(
    [property: JsonPropertyName("caseId")] string CaseId,
    [property: JsonPropertyName("metric")] string Metric,
    [property: JsonPropertyName("scores")] IReadOnlyList<double> Scores,
    [property: JsonPropertyName("mean")] double Mean,
    [property: JsonPropertyName("standardDeviation")] double StandardDeviation,
    [property: JsonPropertyName("range")] double Range,
    [property: JsonPropertyName("verdictFlipped")] bool VerdictFlipped);

/// <summary>How stable the judge is for one metric.</summary>
public sealed record ConsistencySummary
{
    [JsonPropertyName("metric")]
    public required string Metric { get; init; }

    [JsonPropertyName("repetitions")]
    public required int Repetitions { get; init; }

    [JsonPropertyName("meanStandardDeviation")]
    public required double MeanStandardDeviation { get; init; }

    [JsonPropertyName("worstRange")]
    public required double WorstRange { get; init; }

    /// <summary>
    /// Fraction of cases where repeated judging would not agree on blocking the merge.
    /// </summary>
    /// <remarks>
    /// The decision-relevant number. A metric can look stable on average and still flip a gate,
    /// because only the side of the threshold matters, not the size of the wobble.
    /// </remarks>
    [JsonPropertyName("verdictFlipRate")]
    public required double VerdictFlipRate { get; init; }

    [JsonPropertyName("cases")]
    public required IReadOnlyList<CaseConsistency> Cases { get; init; }
}

/// <summary>
/// Measures whether the judge gives the same answer twice.
/// </summary>
/// <remarks>
/// Single-pass gating is only defensible if repeated judging agrees. Calibration compares the judge
/// with a human; this compares the judge with itself, which is a precondition for the first
/// comparison meaning anything. Responses must not be served from cache here, or every repetition
/// returns the same stored answer and the measurement is vacuous.
/// </remarks>
public static class ConsistencyMetrics
{
    public static IReadOnlyList<ConsistencySummary> Summarize(
        IReadOnlyList<ScorePair> pairs,
        TriadThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(thresholds);

        List<ConsistencySummary> summaries = [];

        foreach (string metric in new[]
                 { TriadMetrics.Retrieval, TriadMetrics.Groundedness, TriadMetrics.Relevance })
        {
            ThresholdBand band = thresholds.For(metric);

            CaseConsistency[] cases = pairs
                .Where(pair => pair.Metric == metric && pair.Judge.HasValue)
                .GroupBy(pair => pair.CaseId, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    double[] scores = group.Select(pair => pair.Judge!.Value).ToArray();
                    bool flipped = scores
                        .Select(score => band.Classify(score) == TriadVerdict.Fail)
                        .Distinct()
                        .Count() > 1;

                    return new CaseConsistency(
                        group.Key,
                        metric,
                        scores,
                        scores.Average(),
                        StandardDeviation(scores),
                        scores.Max() - scores.Min(),
                        flipped);
                })
                .OrderBy(item => item.CaseId, StringComparer.Ordinal)
                .ToArray();

            summaries.Add(new ConsistencySummary
            {
                Metric = metric,
                Repetitions = cases.Length == 0 ? 0 : cases.Max(item => item.Scores.Count),
                MeanStandardDeviation = cases.Length == 0 ? 0 : cases.Average(item => item.StandardDeviation),
                WorstRange = cases.Length == 0 ? 0 : cases.Max(item => item.Range),
                VerdictFlipRate = cases.Length == 0
                    ? 0
                    : cases.Count(item => item.VerdictFlipped) / (double)cases.Length,
                Cases = cases
            });
        }

        return summaries;
    }

    /// <summary>Population standard deviation; the repetitions are the whole sample.</summary>
    internal static double StandardDeviation(IReadOnlyList<double> values)
    {
        if (values.Count < 2)
        {
            return 0;
        }

        double mean = values.Average();

        return Math.Sqrt(values.Sum(value => (value - mean) * (value - mean)) / values.Count);
    }
}
