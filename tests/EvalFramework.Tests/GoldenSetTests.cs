using EvalFramework.Datasets;
using EvalFramework.Rules;

namespace EvalFramework.Tests;

/// <summary>
/// Guards the shipped dataset itself. A golden set that no longer parses, or whose frozen
/// responses no longer satisfy their own rules, is a broken contract.
/// </summary>
public sealed class GoldenSetTests
{
    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "datasets")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    [Fact]
    public void ShippedGoldenSetLoadsAndCoversCriticalBehaviour()
    {
        IReadOnlyList<GoldenCase> cases = GoldenSet.Load(Path.Combine(RepoRoot(), "datasets", "support-golden-set.jsonl"));

        Assert.NotEmpty(cases);
        Assert.Contains(cases, item => item.Critical);
        Assert.All(cases, item => Assert.False(string.IsNullOrWhiteSpace(item.Query)));
    }

    [Fact]
    public void EveryPositiveFixtureSatisfiesItsOwnCase()
    {
        string root = RepoRoot();
        IReadOnlyList<GoldenCase> cases = GoldenSet.Load(Path.Combine(root, "datasets", "support-golden-set.jsonl"));
        PositiveFixtureSet positives = PositiveFixtureSet.Load(
            Path.Combine(root, "datasets", "positive-fixtures.json"));

        Dictionary<string, string> lookup = positives.Fixtures
            .ToDictionary(item => item.CaseId, item => item.Response, StringComparer.OrdinalIgnoreCase);

        foreach (GoldenCase goldenCase in cases)
        {
            Assert.True(lookup.ContainsKey(goldenCase.Id), $"No positive fixture for {goldenCase.Id}.");

            RuleReport result = ResponseRules.Evaluate(goldenCase.ToRuleSet(), lookup[goldenCase.Id]);

            Assert.True(
                result.Passed,
                $"{goldenCase.Id} failed: {string.Join(", ", result.Failures.Select(failure => failure.Name))}");
        }
    }

    [Fact]
    public void DuplicateCaseIdsAreRejected()
    {
        string path = Path.GetTempFileName();
        File.WriteAllLines(path,
        [
            """{"id":"dupe","query":"one"}""",
            """{"id":"dupe","query":"two"}"""
        ]);

        try
        {
            Assert.Throws<InvalidDataException>(() => GoldenSet.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}


