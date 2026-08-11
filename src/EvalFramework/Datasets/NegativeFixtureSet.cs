using System.Text.Json;
using System.Text.Json.Serialization;

namespace EvalFramework.Datasets;

/// <summary>A response that must fail, and the rules that must catch it.</summary>
public sealed class NegativeFixture
{
    [JsonPropertyName("caseId")]
    public required string CaseId { get; init; }

    /// <summary>Short description of the defect being simulated.</summary>
    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("response")]
    public required string Response { get; init; }

    /// <summary>Rule names that must appear among the failures.</summary>
    [JsonPropertyName("expectedFailures")]
    public IReadOnlyList<string> ExpectedFailures { get; init; } = [];
}

/// <summary>
/// Known-bad responses paired with the rules that should reject them.
/// </summary>
/// <remarks>
/// Positive fixtures alone only prove the rules do not fire on good output. Without negative
/// fixtures, deleting every rule body and returning "passed" would leave the suite green. These
/// measure discriminative power: whether the rules can tell good from bad, and whether they catch
/// it for the stated reason rather than by accident.
/// </remarks>
public sealed class NegativeFixtureSet
{
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("fixtures")]
    public IReadOnlyList<NegativeFixture> Fixtures { get; init; } = [];

    public static NegativeFixtureSet Load(string path)
    {
        using FileStream stream = File.OpenRead(path);

        return JsonSerializer.Deserialize<NegativeFixtureSet>(stream, JsonDefaults.Options)
            ?? throw new InvalidDataException($"{path} is not a valid negative fixture set.");
    }
}
