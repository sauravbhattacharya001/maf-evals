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

    /// <summary>
    /// Combines the retrievals from every turn of a conversation.
    /// </summary>
    /// <remarks>
    /// A conversation searches the knowledge base once per turn. Keeping only the last search would
    /// make a retrieval expectation meaningless, because the document that answered the customer may
    /// have been found two turns earlier. Duplicate chunks keep their best score.
    /// </remarks>
    public static RetrievalTrace Merge(IReadOnlyList<RetrievalTrace> traces)
    {
        if (traces.Count == 1)
        {
            return traces[0];
        }

        RetrievedChunk[] chunks = traces
            .SelectMany(trace => trace.Chunks)
            .GroupBy(chunk => chunk.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(chunk => chunk.Score).First())
            .OrderByDescending(chunk => chunk.Score)
            .ToArray();

        return new RetrievalTrace(traces.Count == 0 ? string.Empty : traces[^1].Query, chunks);
    }

    /// <summary>Chunk texts, for <c>RetrievalEvaluatorContext</c>.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> ChunkTexts => Chunks.Select(chunk => chunk.Text).ToArray();

    /// <summary>All context as one string, for <c>GroundednessEvaluatorContext</c>.</summary>
    [JsonIgnore]
    public string Combined => string.Join("\n\n", Chunks.Select(chunk => $"{chunk.Title}\n{chunk.Text}"));
}

