using System.Text.Json;
using System.Text.Json.Serialization;

namespace EvalFramework.Datasets;

/// <summary>A known-good response for a golden case.</summary>
public sealed class PositiveFixture
{
    [JsonPropertyName("caseId")]
    public required string CaseId { get; init; }

    [JsonPropertyName("response")]
    public required string Response { get; init; }
}

/// <summary>
/// Responses that must pass, paired with <see cref="NegativeFixtureSet"/>, which must fail.
/// </summary>
/// <remarks>
/// Together the two sets measure both halves of a rule: that it accepts correct output and that it
/// rejects incorrect output. Either alone is misleading. Positives on their own would stay green if
/// every rule were deleted; negatives on their own would stay green if every rule rejected
/// everything. Neither set involves a model, so both run free on every pull request.
/// </remarks>
public sealed class PositiveFixtureSet
{
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("fixtures")]
    public IReadOnlyList<PositiveFixture> Fixtures { get; init; } = [];

    public static PositiveFixtureSet Load(string path)
    {
        using FileStream stream = File.OpenRead(path);

        return JsonSerializer.Deserialize<PositiveFixtureSet>(stream, JsonDefaults.Options)
            ?? throw new InvalidDataException($"{path} is not a valid positive fixture set.");
    }
}
