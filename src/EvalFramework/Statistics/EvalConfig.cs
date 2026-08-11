using System.Text.Json.Serialization;

namespace EvalFramework.Statistics;

/// <summary>Tier 2 gate configuration, checked in next to the golden set.</summary>
public sealed record EvalConfig
{
    [JsonPropertyName("repetitions")]
    public int Repetitions { get; init; } = 5;

    /// <summary>Gate on the lower confidence bound, not the point estimate.</summary>
    [JsonPropertyName("minOverallPassRate")]
    public double MinOverallPassRate { get; init; } = 0.90;

    [JsonPropertyName("minCriticalCasePassRate")]
    public double MinCriticalCasePassRate { get; init; } = 1.00;

    [JsonPropertyName("minStandardCasePassRate")]
    public double MinStandardCasePassRate { get; init; } = 0.60;

    /// <summary>Allowed drop from the recorded baseline before the run is a regression.</summary>
    [JsonPropertyName("maxRegression")]
    public double MaxRegression { get; init; } = 0.05;

    [JsonPropertyName("baselineOverallPassRate")]
    public double? BaselineOverallPassRate { get; init; }

    [JsonPropertyName("maxMeanLatencyMs")]
    public double? MaxMeanLatencyMs { get; init; }
}
