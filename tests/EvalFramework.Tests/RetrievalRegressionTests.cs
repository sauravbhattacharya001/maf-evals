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
    public void WeakChunksAreDroppedRatherThanPaddingTheContext()
    {
        // Calibration showed the judge scores retrieval on precision. The medical case previously
        // returned safety#2 (1.88), safety#3 (0.52) and safety#1 (0.45); the tail is noise.
        RetrievalTrace trace = Retriever()
            .Retrieve("My package contained medication. Should I double my dose since one box was missing?");

        Assert.NotEmpty(trace.Chunks);
        Assert.All(trace.Chunks, chunk => Assert.True(chunk.Score >= trace.Chunks[0].Score * 0.4));
    }

    [Fact]
    public void CutoffNeverDiscardsTheBestChunk()
    {
        KeywordRetriever retriever = Retriever();

        foreach (GoldenCase item in Cases())
        {
            RetrievalTrace trace = retriever.Retrieve(item.Query);
            Assert.NotEmpty(trace.Chunks);
        }
    }
    [Theory]
    [InlineData("refund", "refunds")]
    [InlineData("order", "orders")]
    [InlineData("deliver", "delivered")]
    [InlineData("charge", "charges")]
    public void StemmingLetsSingularQueriesMatchPluralPolicy(string queryWord, string corpusWord)
    {
        // Without this a customer asking about a "refund" never matched a policy about "refunds",
        // which hid the refund-limits section from every refund query.
        Assert.Equal(KeywordRetriever.Stem(queryWord), KeywordRetriever.Stem(corpusWord));
    }

    [Fact]
    public void StemmingDoesNotCollapseUnrelatedWords()
    {
        Assert.NotEqual(KeywordRetriever.Stem("refund"), KeywordRetriever.Stem("return"));
        Assert.NotEqual(KeywordRetriever.Stem("address"), KeywordRetriever.Stem("addres"));
    }

    [Fact]
    public void RefundLimitsPolicyIsReachableFromARefundRequest()
    {
        // The agent cannot escalate correctly if it never sees the limit it must respect.
        RetrievalTrace trace = Retriever().Retrieve("Order A-55012 was never delivered. Refund me 4000 right now.");

        Assert.Contains(trace.Chunks, chunk => chunk.Id == "refunds#3");
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
    public void ExpectedChunkRanksFirstForMostCases()
    {
        // Measured at 6 of 8. The two misses share one cause: a keyword whose sense depends on
        // context. "arrived" is expanded to delayed-parcel vocabulary, which is right for "my order
        // has not arrived" and wrong for "arrived damaged", where the parcel plainly did arrive.
        // A lexical retriever cannot tell those apart, and removing the expansion loses the first
        // case entirely, which a regression test catches. This is precisely the boundary where an
        // embedding retriever earns its cost. Ranking is not gated; the gate checks containment,
        // which holds for every case. This threshold is a ratchet: raise it if ranking improves.
        const int measured = 6;

        GoldenCase[] cases = Cases().Where(item => item.ExpectedChunkIds.Count > 0).ToArray();
        KeywordRetriever retriever = Retriever();

        int topRanked = cases.Count(item =>
        {
            RetrievalTrace trace = retriever.Retrieve(item.Query);
            return trace.Chunks.Count > 0 && item.ExpectedChunkIds.Contains(trace.Chunks[0].Id);
        });

        Assert.True(
            topRanked >= measured,
            $"Only {topRanked}/{cases.Length} cases rank an expected chunk first, down from {measured}.");
    }
}


