using System.Text.Json;
using System.Text.Json.Serialization;

namespace EvalFramework;

public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Severities and verdicts are written as names so artifacts stay readable in diffs.
        Converters = { new JsonStringEnumConverter() }
    };
}
