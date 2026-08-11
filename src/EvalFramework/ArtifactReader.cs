using System.Text.Json;
using EvalFramework.Execution;
using EvalFramework.Statistics;

namespace EvalFramework;

/// <summary>
/// Reads saved artifacts by inspecting their declared schema version.
/// </summary>
/// <remarks>
/// Two artifact shapes are written to the same directory: a bare <see cref="RunArtifact"/> for
/// Tier 3 and a <see cref="Tier2Result"/> wrapping one for Tier 2. Guessing the type from the file
/// name produced an unhandled exception with no exit code. Every artifact declares its schema, so
/// the reader dispatches on that and fails with a diagnosis when it cannot.
/// </remarks>
public static class ArtifactReader
{
    /// <summary>The declared schema of a saved artifact, used to pick the right report.</summary>
    public static string SchemaOf(string path) =>
        ReadSchemaVersion(File.ReadAllText(path))
        ?? throw new InvalidOperationException($"{path} has no schemaVersion.");

    /// <summary>Extracts the run from either artifact shape.</summary>
    public static RunArtifact ReadRun(string path)
    {
        string json = File.ReadAllText(path);
        string? schema = ReadSchemaVersion(json);

        return schema switch
        {
            Tier2Result.CurrentSchemaVersion => ReadTier2(json, path).Run,
            RunArtifact.CurrentSchemaVersion => Deserialize<RunArtifact>(json, path),
            null => throw new InvalidOperationException(
                $"{path} has no schemaVersion, so its type cannot be determined."),
            _ => throw new InvalidOperationException(
                $"{path} declares unsupported schema '{schema}'. Supported: " +
                $"'{RunArtifact.CurrentSchemaVersion}', '{Tier2Result.CurrentSchemaVersion}'.")
        };
    }

    public static Tier2Result ReadTier2(string path) => ReadTier2(File.ReadAllText(path), path);

    private static Tier2Result ReadTier2(string json, string path) =>
        Deserialize<Tier2Result>(json, path);

    internal static string? ReadSchemaVersion(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        return document.RootElement.TryGetProperty("schemaVersion", out JsonElement value)
            ? value.GetString()
            : null;
    }

    private static T Deserialize<T>(string json, string path)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonDefaults.Options)
                ?? throw new InvalidOperationException($"{path} deserialised to null.");
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException($"{path} is not a valid {typeof(T).Name}: {error.Message}");
        }
    }
}
