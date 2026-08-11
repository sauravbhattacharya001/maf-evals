using System.Text.Json;
using System.Text.Json.Serialization;

namespace EvalFramework.Datasets;

/// <summary>
/// A tool the agent is expected to call for a case.
/// </summary>
/// <remarks>
/// Arguments are matched as a subset: the listed keys must be present with matching values, and
/// extra arguments are ignored. Demanding an exact argument set would make the check brittle against
/// harmless additions, while ignoring arguments entirely would pass an agent that called the right
/// tool with the wrong order id.
/// </remarks>
public sealed record ExpectedToolCall
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("arguments")]
    public IReadOnlyDictionary<string, JsonElement> Arguments { get; init; } =
        new Dictionary<string, JsonElement>();
}
