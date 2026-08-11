using System.Text.Json.Serialization;
using EvalFramework.Retrieval;
using EvalFramework.Rules;

namespace EvalFramework.Execution;

/// <summary>
/// One agent invocation, retained so the triad and Tier 3 never have to rerun the agent.
/// </summary>
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

    [JsonPropertyName("rules")]
    public required RuleReport Rules { get; init; }

    /// <summary>Context the agent actually saw. Required for the RAG triad.</summary>
    [JsonPropertyName("retrieval")]
    public RetrievalTrace? Retrieval { get; init; }

    /// <summary>Tier 1 attempts spent on this response. Greater than one means a guard fired.</summary>
    [JsonPropertyName("attempts")]
    public int Attempts { get; init; } = 1;

    /// <summary>Tool calls Tier 1 rejected before they ran.</summary>
    [JsonPropertyName("rejectedToolCalls")]
    public IReadOnlyList<string> RejectedToolCalls { get; init; } = [];

    /// <summary>Set when Tier 1 blocked the response outright.</summary>
    [JsonPropertyName("blocked")]
    public bool Blocked { get; init; }
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

/// <summary>Versioned run artifact. Input to the triad, to Tier 3, and to reporting.</summary>
public sealed record RunArtifact
{
    public const string CurrentSchemaVersion = "run/v2";

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    [JsonPropertyName("tier")]
    public required string Tier { get; init; }

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
