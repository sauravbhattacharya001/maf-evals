using System.Text.Json.Serialization;

namespace EvalFramework.Judging;

public sealed record JudgeMetric(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("score")] double? Score,
    [property: JsonPropertyName("reason")] string? Reason);

public sealed record JudgedResponse
{
    [JsonPropertyName("caseId")]
    public required string CaseId { get; init; }

    [JsonPropertyName("repetition")]
    public required int Repetition { get; init; }

    [JsonPropertyName("metrics")]
    public required IReadOnlyList<JudgeMetric> Metrics { get; init; }
}

public sealed record MetricSummary(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("mean")] double Mean,
    [property: JsonPropertyName("min")] double Min,
    [property: JsonPropertyName("scored")] int Scored);

/// <summary>Versioned Tier 3 artifact. Always records who judged and against which rubric.</summary>
public sealed record JudgeArtifact
{
    public const string CurrentSchemaVersion = "tier3/v1";

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("sourceRunId")]
    public required string SourceRunId { get; init; }

    [JsonPropertyName("timestampUtc")]
    public required DateTimeOffset TimestampUtc { get; init; }

    [JsonPropertyName("judgeModel")]
    public required string JudgeModel { get; init; }

    [JsonPropertyName("rubricVersion")]
    public required string RubricVersion { get; init; }

    [JsonPropertyName("summary")]
    public required IReadOnlyList<MetricSummary> Summary { get; init; }

    [JsonPropertyName("judged")]
    public required IReadOnlyList<JudgedResponse> Judged { get; init; }
}
