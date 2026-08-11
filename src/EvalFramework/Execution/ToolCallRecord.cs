using System.Text.Json.Serialization;

namespace EvalFramework.Execution;

/// <summary>
/// A tool invocation as it happened, including calls a guard refused.
/// </summary>
/// <remarks>
/// Rejected calls are kept rather than dropped because "the agent tried to refund 1,200 and was
/// stopped" and "the agent never tried" are different behaviours that a pass rate alone cannot
/// distinguish. Shared by live runs and captured incident traces so both are analysed identically.
/// </remarks>
public sealed record ToolCallRecord(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] IReadOnlyDictionary<string, object?> Arguments,
    [property: JsonPropertyName("rejected")] bool Rejected = false);
