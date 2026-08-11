using System.Text.Json;
using System.Text.Json.Serialization;
using EvalFramework.Retrieval;

namespace EvalFramework.Calibration;

/// <summary>Human scores for one calibration case, on the evaluators' 1 to 5 scale.</summary>
public sealed record HumanLabels
{
    [JsonPropertyName("retrieval")]
    public required double Retrieval { get; init; }

    [JsonPropertyName("groundedness")]
    public required double Groundedness { get; init; }

    [JsonPropertyName("relevance")]
    public required double Relevance { get; init; }

    public double For(string metric) => metric switch
    {
        RagTriad.TriadMetrics.Retrieval => Retrieval,
        RagTriad.TriadMetrics.Groundedness => Groundedness,
        RagTriad.TriadMetrics.Relevance => Relevance,
        _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, "Unknown triad metric.")
    };
}

public sealed record CalibrationChunk(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("text")] string Text);

/// <summary>
/// A hand-labelled example used to check the judge against human judgement.
/// </summary>
/// <remarks>
/// Thresholds are meaningless without this. Saying a score below 3 blocks a merge presumes the
/// judge's 3 means what a reviewer's 3 means, and nothing establishes that until labelled examples
/// exist. The set deliberately decouples the three metrics, including answers that are grounded but
/// irrelevant and answers that are correct yet unsupported by the retrieved context.
/// </remarks>
public sealed record CalibrationCase
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Short description of what this case is designed to probe.</summary>
    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("query")]
    public required string Query { get; init; }

    [JsonPropertyName("response")]
    public required string Response { get; init; }

    [JsonPropertyName("context")]
    public IReadOnlyList<CalibrationChunk> Context { get; init; } = [];

    [JsonPropertyName("labels")]
    public required HumanLabels Labels { get; init; }

    /// <summary>Why the labels are what they are, so a reviewer can disagree specifically.</summary>
    [JsonPropertyName("rationale")]
    public required string Rationale { get; init; }

    public RetrievalTrace ToRetrievalTrace() => new(
        Query,
        Context.Select((chunk, index) =>
            new RetrievedChunk(chunk.Id, chunk.Title, chunk.Text, Context.Count - index)).ToArray());
}

public static class CalibrationSet
{
    public static IReadOnlyList<CalibrationCase> Load(string path)
    {
        List<CalibrationCase> cases = [];
        int lineNumber = 0;

        foreach (string line in File.ReadLines(path))
        {
            lineNumber++;
            string trimmed = line.Trim();

            if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            cases.Add(JsonSerializer.Deserialize<CalibrationCase>(trimmed, JsonDefaults.Options)
                ?? throw new InvalidDataException($"{path}:{lineNumber} is not a valid calibration case."));
        }

        if (cases.Count == 0)
        {
            throw new InvalidDataException($"No calibration cases found in {path}.");
        }

        return cases;
    }
}
