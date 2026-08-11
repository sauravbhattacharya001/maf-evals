using System.Text.Json.Serialization;
using EvalFramework.RagTriad;

namespace EvalFramework.Statistics;

/// <summary>Gate configuration, checked in next to the golden set.</summary>
public sealed record EvalConfig
{
    /// <summary>Tier 2 gates a pull request, so it runs each case once.</summary>
    [JsonPropertyName("tier2Repetitions")]
    public int Tier2Repetitions { get; init; } = 1;

    /// <summary>Tier 3 measures reliability, so it needs repetition.</summary>
    [JsonPropertyName("tier3Repetitions")]
    public int Tier3Repetitions { get; init; } = 5;

    [JsonPropertyName("triad")]
    public TriadThresholds Triad { get; init; } = new();

    /// <summary>Gate on the lower confidence bound, not the point estimate. Tier 3 only.</summary>
    [JsonPropertyName("minOverallPassRate")]
    public double MinOverallPassRate { get; init; } = 0.80;

    [JsonPropertyName("minCriticalCasePassRate")]
    public double MinCriticalCasePassRate { get; init; } = 1.00;

    [JsonPropertyName("minStandardCasePassRate")]
    public double MinStandardCasePassRate { get; init; } = 0.60;

    /// <summary>Allowed drop from the recorded baseline before the run is a regression.</summary>
    [JsonPropertyName("maxRegression")]
    public double MaxRegression { get; init; } = 0.05;

    [JsonPropertyName("baselineOverallPassRate")]
    public double? BaselineOverallPassRate { get; init; }

    /// <summary>
    /// Fraction of invocations allowed to fail for infrastructure reasons before the run is
    /// declared untrustworthy. Defaults to zero: a partial run should not quietly become a verdict.
    /// </summary>
    [JsonPropertyName("maxErrorRate")]
    public double MaxErrorRate { get; init; }

    /// <summary>Seconds before a single agent or judge call is abandoned as errored.</summary>
    [JsonPropertyName("callTimeoutSeconds")]
    public int CallTimeoutSeconds { get; init; } = 120;

    /// <summary>Concurrent judge calls. Bounded to stay inside provider rate limits.</summary>
    [JsonPropertyName("judgeConcurrency")]
    public int JudgeConcurrency { get; init; } = 4;

    [JsonPropertyName("maxMeanLatencyMs")]
    public double? MaxMeanLatencyMs { get; init; }
}

