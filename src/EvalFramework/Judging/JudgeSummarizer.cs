namespace EvalFramework.Judging;

/// <summary>Aggregates judge scores. Pure so it can be tested without a judge model.</summary>
public static class JudgeSummarizer
{
    public static IReadOnlyList<MetricSummary> Summarize(IReadOnlyList<JudgedResponse> judged)
    {
        return judged
            .SelectMany(response => response.Metrics)
            .Where(metric => metric.Score.HasValue)
            .GroupBy(metric => metric.Name, StringComparer.Ordinal)
            .Select(group => new MetricSummary(
                group.Key,
                group.Average(metric => metric.Score!.Value),
                group.Min(metric => metric.Score!.Value),
                group.Count()))
            .OrderBy(summary => summary.Name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Judge gate. Uses the minimum as well as the mean because a single very poor
    /// response matters more than a comfortable average.
    /// </summary>
    public static IReadOnlyList<string> ApplyThresholds(
        IReadOnlyList<MetricSummary> summary,
        double minMean,
        double minScore)
    {
        List<string> violations = [];

        foreach (MetricSummary metric in summary)
        {
            if (metric.Mean < minMean)
            {
                violations.Add($"{metric.Name} mean {metric.Mean:F2} is below {minMean:F2}");
            }

            if (metric.Min < minScore)
            {
                violations.Add($"{metric.Name} minimum {metric.Min:F2} is below {minScore:F2}");
            }
        }

        return violations;
    }
}
