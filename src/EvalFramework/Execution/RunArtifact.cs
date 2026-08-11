using System.Text.Json.Serialization;
using EvalFramework.Deterministic;

namespace EvalFramework.Execution;

/// <summary>One agent invocation, retained so Tier 3 never has to rerun the agent.</summary>
public sealed record ResponseRecord
{
    [JsonPropertyName("caseId")]
    public required string CaseId { get; init; }

    [JsonPropertyName("query")]
    public required string Query { get; init; }

    [JsonPropertyName("repetition")]
    public required int Repetition { get; init; }

    [JsonPropertyName("response")]
    public required string Response { get; init; }

    [JsonPropertyName("latencyMs")]
    public required double LatencyMs { get; init; }

    [JsonPropertyName("deterministic")]
    public required DeterministicResult Deterministic { get; init; }
}

/// <summary>Per-case aggregate across repetitions.</summary>
public sealed record CaseStatistics
{
    [JsonPropertyName("caseId")]
    public required string CaseId { get; init; }

    [JsonPropertyName("critical")]
    public required bool Critical { get; init; }

    [JsonPropertyName("trials")]
    public required int Trials { get; init; }

    [JsonPropertyName("passes")]
    public required int Passes { get; init; }

    [JsonPropertyName("passRate")]
    public required double PassRate { get; init; }

    [JsonPropertyName("lowerBound")]
    public required double LowerBound { get; init; }

    [JsonPropertyName("upperBound")]
    public required double UpperBound { get; init; }

    /// <summary>True when a case both passes and fails across repetitions.</summary>
    [JsonPropertyName("flaky")]
    public required bool Flaky { get; init; }

    [JsonPropertyName("meanLatencyMs")]
    public required double MeanLatencyMs { get; init; }

    [JsonPropertyName("topFailures")]
    public IReadOnlyList<string> TopFailures { get; init; } = [];
}

/// <summary>Versioned Tier 2 artifact. Input to Tier 3 and to reporting.</summary>
public sealed record RunArtifact
{
    public const string CurrentSchemaVersion = "tier2/v1";

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    [JsonPropertyName("timestampUtc")]
    public required DateTimeOffset TimestampUtc { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("datasetPath")]
    public required string DatasetPath { get; init; }

    [JsonPropertyName("repetitions")]
    public required int Repetitions { get; init; }

    [JsonPropertyName("overallPassRate")]
    public required double OverallPassRate { get; init; }

    [JsonPropertyName("overallLowerBound")]
    public required double OverallLowerBound { get; init; }

    [JsonPropertyName("overallUpperBound")]
    public required double OverallUpperBound { get; init; }

    [JsonPropertyName("meanLatencyMs")]
    public required double MeanLatencyMs { get; init; }

    [JsonPropertyName("cases")]
    public required IReadOnlyList<CaseStatistics> Cases { get; init; }

    [JsonPropertyName("responses")]
    public required IReadOnlyList<ResponseRecord> Responses { get; init; }
}
