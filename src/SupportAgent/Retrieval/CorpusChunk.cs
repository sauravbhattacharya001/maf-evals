using System.Text.Json.Serialization;

namespace SupportAgent.Retrieval;

/// <summary>A retrievable section of the knowledge base.</summary>
public sealed record CorpusChunk(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("text")] string Text);
