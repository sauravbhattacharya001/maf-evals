using System.Text.Json;
using System.Text.Json.Serialization;

namespace EvalFramework.Datasets;

public sealed class RecordedResponse
{
    [JsonPropertyName("caseId")]
    public required string CaseId { get; init; }

    [JsonPropertyName("response")]
    public required string Response { get; init; }
}

public sealed class RecordedResponseSet
{
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("responses")]
    public IReadOnlyList<RecordedResponse> Responses { get; init; } = [];

    public static RecordedResponseSet Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<RecordedResponseSet>(stream, JsonDefaults.Options)
            ?? throw new InvalidDataException($"{path} is not a valid recorded response set.");
    }
}
