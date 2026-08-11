using EvalFramework.Datasets;
using SupportAgent.Retrieval;

namespace EvalFramework.Tests;

/// <summary>
/// Health checks on the golden set itself.
/// </summary>
/// <remarks>
/// A suite is only as good as the dataset behind it. A set can be green while being redundant,
/// unable to fail, or blind to half the knowledge base, and none of those show up as a failing
/// test. These checks make dataset decay visible instead of letting coverage quietly rot as the
/// corpus grows.
/// </remarks>
public sealed class GoldenSetHealthTests
{
    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "corpus")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("corpus not found.");
    }

    private static IReadOnlyList<GoldenCase> Cases() =>
        GoldenSet.Load(Path.Combine(RepoRoot(), "datasets", "support-golden-set.jsonl"));

    private static IReadOnlyList<CorpusChunk> Corpus() =>
        CorpusLoader.Load(Path.Combine(RepoRoot(), "corpus"));

    private static HashSet<string> Tokens(string text) =>
        text.ToLowerInvariant()
            .Split([' ', '.', ',', '?', '!', ';', ':', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length > 3)
            .ToHashSet(StringComparer.Ordinal);

    private static double Jaccard(HashSet<string> left, HashSet<string> right)
    {
        int union = left.Union(right, StringComparer.Ordinal).Count();

        return union == 0 ? 0 : left.Intersect(right, StringComparer.Ordinal).Count() / (double)union;
    }

    [Fact]
    public void QueriesAreNotDuplicated()
    {
        string[] queries = Cases().Select(item => item.Query.Trim()).ToArray();

        Assert.Equal(queries.Length, queries.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void QueriesAreNotNearDuplicatesOfEachOther()
    {
        // Two cases asking the same thing in different words inflate the pass rate without
        // adding coverage, and make a single defect look like two.
        GoldenCase[] cases = Cases().ToArray();
        List<string> tooSimilar = [];

        for (int i = 0; i < cases.Length; i++)
        {
            for (int j = i + 1; j < cases.Length; j++)
            {
                double similarity = Jaccard(Tokens(cases[i].Query), Tokens(cases[j].Query));

                if (similarity > 0.6)
                {
                    tooSimilar.Add($"{cases[i].Id} and {cases[j].Id} ({similarity:P0} token overlap)");
                }
            }
        }

        Assert.True(tooSimilar.Count == 0, $"Near-duplicate cases: {string.Join("; ", tooSimilar)}");
    }

    [Fact]
    public void EveryCorpusDocumentIsExercisedBySomeCase()
    {
        // A document no case touches is untested knowledge: it can rot, contradict itself, or be
        // deleted, and nothing in the suite notices.
        HashSet<string> exercised = Cases()
            .SelectMany(item => item.ExpectedChunkIds)
            .Select(id => id.Split('#')[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string[] untouched = Corpus()
            .Select(chunk => chunk.Id.Split('#')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(document => !exercised.Contains(document))
            .ToArray();

        Assert.True(untouched.Length == 0, $"Corpus documents no case exercises: {string.Join(", ", untouched)}");
    }

    [Fact]
    public void EveryCaseCanActuallyFailOnContent()
    {
        // A case whose only rule is a length floor passes for almost any text, so it measures
        // nothing about correctness.
        string[] weak = Cases()
            .Where(item => item.ExpectedTerms.Count == 0
                && item.ExpectedAnyTerms.Count == 0
                && item.ForbiddenTerms.Count == 0)
            .Select(item => item.Id)
            .ToArray();

        Assert.True(weak.Length == 0, $"Cases with no content rule: {string.Join(", ", weak)}");
    }

    [Fact]
    public void ExpectedAndForbiddenTermsDoNotContradictEachOther()
    {
        foreach (GoldenCase item in Cases())
        {
            IEnumerable<string> expected = item.ExpectedTerms.Concat(item.ExpectedAnyTerms.SelectMany(g => g));

            foreach (string term in expected)
            {
                Assert.DoesNotContain(
                    item.ForbiddenTerms,
                    forbidden => forbidden.Equals(term, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    [Fact]
    public void SafetyCriticalBehaviourIsMarkedCritical()
    {
        // Critical cases face the strict Tier 3 gate. If a safety case is not marked, a regression
        // in refusing medical advice would be tolerated as ordinary flakiness.
        GoldenCase[] safety = Cases()
            .Where(item => item.ExpectedChunkIds.Any(id => id.StartsWith("safety", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.NotEmpty(safety);
        Assert.All(safety, item => Assert.True(item.Critical, $"{item.Id} touches safety but is not critical."));
    }

    [Fact]
    public void MinimumLengthsAreSetDeliberately()
    {
        Assert.All(Cases(), item => Assert.InRange(item.MinLength, 1, 2000));
    }

    [Fact]
    public void TheSetContainsBothCriticalAndOrdinaryCases()
    {
        GoldenCase[] cases = Cases().ToArray();

        Assert.Contains(cases, item => item.Critical);
        Assert.Contains(cases, item => !item.Critical);
    }
}
