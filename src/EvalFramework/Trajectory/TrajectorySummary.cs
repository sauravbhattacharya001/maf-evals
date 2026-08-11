using System.Text.Json.Serialization;

namespace EvalFramework.Trajectory;

/// <summary>One judged aspect of the agent's reasoning path.</summary>
public sealed record TrajectoryScore(
    [property: JsonPropertyName("metric")] string Metric,
    [property: JsonPropertyName("score")] double? Score,
    [property: JsonPropertyName("reason")] string? Reason);

/// <summary>The trajectory judgement for one run of one case.</summary>
public sealed record TrajectoryResult(
    [property: JsonPropertyName("caseId")] string CaseId,
    [property: JsonPropertyName("repetition")] int Repetition,
    [property: JsonPropertyName("scores")] IReadOnlyList<TrajectoryScore> Scores);

/// <summary>Distribution of one metric across judged trajectories.</summary>
public sealed record TrajectoryMetricSummary
{
    [JsonPropertyName("metric")]
    public required string Metric { get; init; }

    [JsonPropertyName("judged")]
    public required int Judged { get; init; }

    [JsonPropertyName("mean")]
    public required double Mean { get; init; }

    [JsonPropertyName("standardDeviation")]
    public required double StandardDeviation { get; init; }

    [JsonPropertyName("min")]
    public required double Min { get; init; }

    /// <summary>
    /// The scale this metric is reported on, because they differ and a bare mean would mislead.
    /// </summary>
    [JsonPropertyName("scale")]
    public required string Scale { get; init; }

    /// <summary>Cases whose worst score sits at or below this metric's floor, worth reading.</summary>
    [JsonPropertyName("worstCases")]
    public IReadOnlyList<string> WorstCases { get; init; } = [];
}

public static class TrajectoryMetrics
{
    public const string IntentResolution = "Intent Resolution";
    public const string TaskAdherence = "Task Adherence";
    public const string ToolCallAccuracy = "Tool Call Accuracy";

    /// <summary>
    /// Intent resolution and task adherence are rated 1 to 5; tool call accuracy is a pass rate.
    /// Averaging them together, or reading 0.75 as a poor rating rather than three calls in four,
    /// would be an easy and expensive misreading.
    /// </summary>
    public static string ScaleOf(string metric) =>
        metric == ToolCallAccuracy ? "0-1 pass rate" : "1-5 rating";
}

/// <summary>
/// Aggregates trajectory scores into a trend rather than a verdict.
/// </summary>
/// <remarks>
/// Reported as a distribution because a single judge score is not trustworthy on its own: measured
/// judge instability reaches a three point swing on identical input. A mean with a spread over many
/// judged trajectories is a claim that survives that noise, which a per-run number does not. This is
/// why trajectory quality is monitored on a schedule rather than gating a merge.
/// </remarks>
public static class TrajectorySummary
{
    public static IReadOnlyList<TrajectoryMetricSummary> Summarize(
        IReadOnlyList<TrajectoryResult> results,
        double worstCaseThreshold = 3.0)
    {
        ArgumentNullException.ThrowIfNull(results);

        return results
            .SelectMany(result => result.Scores.Select(score => (result.CaseId, score)))
            .Where(item => item.score.Score.HasValue)
            .GroupBy(item => item.score.Metric, StringComparer.Ordinal)
            .Select(group =>
            {
                double[] values = group.Select(item => item.score.Score!.Value).ToArray();
                double mean = values.Average();

                return new TrajectoryMetricSummary
                {
                    Metric = group.Key,
                    Judged = values.Length,
                    Scale = TrajectoryMetrics.ScaleOf(group.Key),
                    Mean = mean,
                    StandardDeviation = StandardDeviation(values, mean),
                    Min = values.Min(),
                    WorstCases = group
                        .Where(item => item.score.Score!.Value <=
                            (group.Key == TrajectoryMetrics.ToolCallAccuracy ? 0d : worstCaseThreshold))
                        .Select(item => item.CaseId)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(id => id, StringComparer.Ordinal)
                        .ToArray()
                };
            })
            .OrderBy(summary => summary.Metric, StringComparer.Ordinal)
            .ToArray();
    }

    private static double StandardDeviation(IReadOnlyList<double> values, double mean) =>
        values.Count < 2
            ? 0
            : Math.Sqrt(values.Sum(value => (value - mean) * (value - mean)) / values.Count);
}

