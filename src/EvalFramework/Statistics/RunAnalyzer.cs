using EvalFramework.Datasets;
using EvalFramework.Execution;

namespace EvalFramework.Statistics;

public sealed record GateViolation(string Gate, string Detail);

public sealed record GateReport(IReadOnlyList<GateViolation> Violations)
{
    public bool Passed => Violations.Count == 0;
}

/// <summary>
/// Turns raw repetition outcomes into statistics and applies Tier 2 gates.
/// Kept free of I/O so it is fully unit testable.
/// </summary>
public static class RunAnalyzer
{
    public static IReadOnlyList<CaseStatistics> Summarize(
        IReadOnlyList<GoldenCase> cases,
        IReadOnlyList<ResponseRecord> responses)
    {
        Dictionary<string, GoldenCase> lookup = cases.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);

        return responses
            .GroupBy(record => record.CaseId, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                ResponseRecord[] records = group.ToArray();
                int trials = records.Length;
                int passes = records.Count(record => record.Deterministic.Passed);
                ConfidenceInterval interval = Wilson.Interval(passes, trials);

                string[] topFailures = records
                    .SelectMany(record => record.Deterministic.Failures)
                    .GroupBy(failure => failure.Name, StringComparer.Ordinal)
                    .OrderByDescending(failures => failures.Count())
                    .Select(failures => $"{failures.Key} x{failures.Count()}")
                    .ToArray();

                return new CaseStatistics
                {
                    CaseId = group.Key,
                    Critical = lookup.TryGetValue(group.Key, out GoldenCase? match) && match.Critical,
                    Trials = trials,
                    Passes = passes,
                    PassRate = trials == 0 ? 0d : (double)passes / trials,
                    LowerBound = interval.Lower,
                    UpperBound = interval.Upper,
                    Flaky = passes > 0 && passes < trials,
                    MeanLatencyMs = records.Average(record => record.LatencyMs),
                    TopFailures = topFailures
                };
            })
            .OrderBy(statistics => statistics.CaseId, StringComparer.Ordinal)
            .ToArray();
    }

    public static GateReport ApplyGates(RunArtifact artifact, EvalConfig config)
    {
        List<GateViolation> violations = [];

        if (artifact.OverallLowerBound < config.MinOverallPassRate)
        {
            violations.Add(new GateViolation(
                "overall_pass_rate",
                $"95% lower bound {artifact.OverallLowerBound:P1} is below required {config.MinOverallPassRate:P1} " +
                $"(observed {artifact.OverallPassRate:P1})"));
        }

        foreach (CaseStatistics statistics in artifact.Cases)
        {
            double required = statistics.Critical ? config.MinCriticalCasePassRate : config.MinStandardCasePassRate;
            if (statistics.PassRate < required)
            {
                violations.Add(new GateViolation(
                    statistics.Critical ? "critical_case" : "standard_case",
                    $"{statistics.CaseId} passed {statistics.PassRate:P1}, required {required:P1}"));
            }
        }

        if (config.BaselineOverallPassRate is double baseline
            && artifact.OverallPassRate < baseline - config.MaxRegression)
        {
            violations.Add(new GateViolation(
                "regression",
                $"observed {artifact.OverallPassRate:P1} is more than {config.MaxRegression:P1} below baseline {baseline:P1}"));
        }

        if (config.MaxMeanLatencyMs is double maxLatency && artifact.MeanLatencyMs > maxLatency)
        {
            violations.Add(new GateViolation(
                "latency",
                $"mean latency {artifact.MeanLatencyMs:F0} ms exceeds budget {maxLatency:F0} ms"));
        }

        return new GateReport(violations);
    }
}
