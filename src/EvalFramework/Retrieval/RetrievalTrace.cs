using System.Text.Json.Serialization;

namespace EvalFramework.Retrieval;

/// <summary>A chunk selected for one query, with the score that selected it.</summary>
public sealed record RetrievedChunk(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("score")] double Score);

/// <summary>
/// What retrieval actually returned for one request.
/// </summary>
/// <remarks>
/// Lives in the eval framework rather than the agent because it is a contract between the two:
/// the agent produces it at request time and Tier 2 evaluates the triad against it. Re-running
/// retrieval later would measure a different system than the one that produced the answer.
/// </remarks>
public sealed record RetrievalTrace(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("chunks")] IReadOnlyList<RetrievedChunk> Chunks)
{
    public static RetrievalTrace Empty(string query) => new(query, []);

    /// <summary>Chunk texts, for <c>RetrievalEvaluatorContext</c>.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> ChunkTexts => Chunks.Select(chunk => chunk.Text).ToArray();

    /// <summary>All context as one string, for <c>GroundednessEvaluatorContext</c>.</summary>
    [JsonIgnore]
    public string Combined => string.Join("\n\n", Chunks.Select(chunk => $"{chunk.Title}\n{chunk.Text}"));
}
