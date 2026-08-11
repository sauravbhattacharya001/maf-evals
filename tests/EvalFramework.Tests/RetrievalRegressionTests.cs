using EvalFramework.Datasets;
using EvalFramework.Retrieval;
using SupportAgent.Retrieval;

namespace EvalFramework.Tests;

/// <summary>
/// Runs the retriever over every golden case offline.
/// </summary>
/// <remarks>
/// The `expectedChunkIds` check previously existed only inside paid Tier 2, so the first sign of a
/// retrieval regression was a failed gate after spending money on both the agent and the judge. The
/// check needs no model, so it belongs in the free suite. This exact assertion would have caught the
/// `shipping#2` miss found by the first live run.
/// </remarks>
public sealed class RetrievalRegressionTests
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

    private static KeywordRetriever Retriever() =>
        KeywordRetriever.FromDirectory(Path.Combine(RepoRoot(), "corpus"));

    public static TheoryData<string> CasesWithExpectations()
    {
        TheoryData<string> data = [];

        foreach (GoldenCase item in Cases().Where(c => c.ExpectedChunkIds.Count > 0))
        {
            data.Add(item.Id);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CasesWithExpectations))]
    public void ExpectedChunksAreRetrievedForEveryGoldenCase(string caseId)
    {
        GoldenCase goldenCase = Cases().Single(item => item.Id == caseId);
        RetrievalTrace trace = Retriever().Retrieve(goldenCase.Query);

        string[] retrieved = trace.Chunks.Select(chunk => chunk.Id).ToArray();
        string[] missing = goldenCase.ExpectedChunkIds.Where(id => !retrieved.Contains(id)).ToArray();

        Assert.True(
            missing.Length == 0,
            $"{caseId}: expected {string.Join(", ", missing)} but retrieved {string.Join(", ", retrieved)}.");
    }

    [Fact]
    public void EveryGoldenCaseDeclaresARetrievalExpectation()
    {
        string[] undeclared = Cases()
            .Where(item => item.ExpectedChunkIds.Count == 0)
            .Select(item => item.Id)
            .ToArray();

        Assert.True(
            undeclared.Length == 0,
            $"Cases with no expectedChunkIds: {string.Join(", ", undeclared)}. " +
            "Without one, a retrieval regression for that case is invisible until a judge notices.");
    }

    [Fact]
    public void ExpectedChunkIdsReferToChunksThatActuallyExist()
    {
        // A typo in an expectation would otherwise look like a permanent retrieval failure.
        HashSet<string> known = CorpusLoader
            .Load(Path.Combine(RepoRoot(), "corpus"))
            .Select(chunk => chunk.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (GoldenCase item in Cases())
        {
            foreach (string id in item.ExpectedChunkIds)
            {
                Assert.True(known.Contains(id), $"{item.Id} expects unknown chunk '{id}'.");
            }
        }
    }

    [Fact]
    public void ExpectedChunkUsuallyRanksFirst()
    {
        // Not a hard rule, but a sharp drop here means ranking has degraded even though the
        // chunk is still somewhere in the results.
        GoldenCase[] cases = Cases().Where(item => item.ExpectedChunkIds.Count > 0).ToArray();
        KeywordRetriever retriever = Retriever();

        int topRanked = cases.Count(item =>
        {
            RetrievalTrace trace = retriever.Retrieve(item.Query);
            return trace.Chunks.Count > 0 && item.ExpectedChunkIds.Contains(trace.Chunks[0].Id);
        });

        Assert.True(
            topRanked >= cases.Length - 1,
            $"Only {topRanked}/{cases.Length} cases rank an expected chunk first.");
    }
}
