using System.Text.Json;
using EvalFramework.Datasets;
using EvalFramework.Execution;
using EvalFramework.RagTriad;
using EvalFramework.Rules;
using EvalFramework.Statistics;

namespace EvalFramework.Tests;

/// <summary>
/// Two artifact shapes share one directory. Guessing between them by file name produced an
/// unhandled crash, so reading must dispatch on the declared schema and fail with a diagnosis.
/// </summary>
public sealed class ArtifactReaderTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("artifact-tests").FullName;

    private static readonly GoldenCase Case = new()
    {
        Id = "case",
        Query = "q",
        MinLength = 1,
        RequireActionableFormat = false
    };

    private static RunArtifact SampleRun()
    {
        ResponseRecord record = new()
        {
            CaseId = Case.Id,
            Query = Case.Query,
            Repetition = 1,
            Response = "an answer",
            LatencyMs = 5,
            Rules = ResponseRules.Evaluate(Case.ToRuleSet(), "an answer")
        };

        return AgentRunner.Build([Case], [record], 1, "tier2", "model", "dataset.jsonl");
    }

    private string Write(string name, object payload)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonDefaults.Options));

        return path;
    }

    [Fact]
    public void RunArtifactIsReadDirectly()
    {
        string path = Write("tier3-x.json", SampleRun());

        Assert.Equal(RunArtifact.CurrentSchemaVersion, ArtifactReader.SchemaOf(path));
        Assert.Equal("tier2", ArtifactReader.ReadRun(path).Tier);
    }

    [Fact]
    public void Tier2ResultIsUnwrappedToItsRun()
    {
        Tier2Result result = Tier2Gate.Apply(SampleRun(), [Case], [], new TriadThresholds());
        string path = Write("tier2-x.json", result);

        Assert.Equal(Tier2Result.CurrentSchemaVersion, ArtifactReader.SchemaOf(path));
        Assert.NotNull(ArtifactReader.ReadRun(path));
        Assert.NotNull(ArtifactReader.ReadTier2(path).Run);
    }

    [Fact]
    public void UnknownSchemaFailsWithADiagnosisRatherThanACrash()
    {
        string path = Path.Combine(_directory, "future.json");
        File.WriteAllText(path, """{"schemaVersion":"run/v99"}""");

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(() => ArtifactReader.ReadRun(path));

        Assert.Contains("run/v99", error.Message, StringComparison.Ordinal);
        Assert.Contains("Supported", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingSchemaIsReportedClearly()
    {
        string path = Path.Combine(_directory, "legacy.json");
        File.WriteAllText(path, """{"runId":"abc"}""");

        Assert.Contains(
            "no schemaVersion",
            Assert.Throws<InvalidOperationException>(() => ArtifactReader.ReadRun(path)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TruncatedArtifactIsReportedAsInvalidNotAsATypeError()
    {
        string path = Path.Combine(_directory, "broken.json");
        File.WriteAllText(path, $$"""{"schemaVersion":"{{RunArtifact.CurrentSchemaVersion}}","runId":"a"}""");

        Assert.Contains(
            "not a valid",
            Assert.Throws<InvalidOperationException>(() => ArtifactReader.ReadRun(path)).Message,
            StringComparison.Ordinal);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
