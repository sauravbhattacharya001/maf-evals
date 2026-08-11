using System.Text.Json;
using System.Text.Json.Serialization;
using EvalFramework.Retrieval;

namespace EvalFramework.Incident;

/// <summary>A tool call as it happened in production.</summary>
public sealed record TracedToolCall(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] IReadOnlyDictionary<string, object?> Arguments);

/// <summary>
/// One captured production interaction, replayed by Tier 3 after an incident.
/// </summary>
/// <remarks>
/// Deliberately a plain data file with no dependency on the running system. An incident is
/// investigated by replaying exactly what happened, not by asking the agent to try again, which
/// would produce a different answer and hide the failure.
/// </remarks>
public sealed record IncidentTrace
{
    public const string CurrentSchemaVersion = "incident/v1";

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("incidentId")]
    public required string IncidentId { get; init; }

    [JsonPropertyName("capturedUtc")]
    public DateTimeOffset CapturedUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Golden case this interaction resembles, when known.</summary>
    [JsonPropertyName("relatedCaseId")]
    public string? RelatedCaseId { get; init; }

    [JsonPropertyName("query")]
    public required string Query { get; init; }

    [JsonPropertyName("response")]
    public required string Response { get; init; }

    [JsonPropertyName("retrieval")]
    public RetrievalTrace? Retrieval { get; init; }

    [JsonPropertyName("toolCalls")]
    public IReadOnlyList<TracedToolCall> ToolCalls { get; init; } = [];

    public static IncidentTrace Load(string path)
    {
        using FileStream stream = File.OpenRead(path);

        return JsonSerializer.Deserialize<IncidentTrace>(stream, JsonDefaults.Options)
            ?? throw new InvalidDataException($"{path} is not a valid incident trace.");
    }
}
