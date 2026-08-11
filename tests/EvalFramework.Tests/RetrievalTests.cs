using EvalFramework.Retrieval;
using SupportAgent.Retrieval;

namespace EvalFramework.Tests;

public sealed class RetrievalTests
{
    private static string CorpusDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "corpus");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the corpus directory.");
    }

    private static KeywordRetriever Retriever() => KeywordRetriever.FromDirectory(CorpusDirectory());

    [Fact]
    public void HeadingsBecomeSeparateChunks()
    {
        string[] lines =
        [
            "# Title",
            "## First",
            "alpha text",
            "## Second",
            "beta text"
        ];

        CorpusChunk[] chunks = CorpusLoader.Split("doc", lines).ToArray();

        Assert.Equal(2, chunks.Length);
        Assert.Equal("First", chunks[0].Title);
        Assert.Equal("alpha text", chunks[0].Text);
        Assert.Equal("doc#2", chunks[1].Id);
    }

    [Fact]
    public void DuplicateChargeQueryRetrievesTheBillingSection()
    {
        RetrievalTrace trace = Retriever().Retrieve("I was charged twice for one order", topK: 3);

        Assert.NotEmpty(trace.Chunks);
        Assert.Contains(trace.Chunks, chunk => chunk.Title.Contains("Duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MedicationQueryRetrievesTheSafetyGuidance()
    {
        RetrievalTrace trace = Retriever().Retrieve("my medication shipment was incomplete, should I change my dose");

        Assert.Contains(trace.Chunks, chunk => chunk.Text.Contains("pharmacist", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnrelatedQueryRetrievesNothingRatherThanTheLeastBadChunk()
    {
        RetrievalTrace trace = Retriever().Retrieve("xylophone quokka zeppelin");

        Assert.Empty(trace.Chunks);
        Assert.Equal(string.Empty, trace.Combined);
    }

    [Fact]
    public void RetrievalIsDeterministicAcrossCalls()
    {
        KeywordRetriever retriever = Retriever();

        string[] first = retriever.Retrieve("refund for a damaged item").Chunks.Select(c => c.Id).ToArray();
        string[] second = retriever.Retrieve("refund for a damaged item").Chunks.Select(c => c.Id).ToArray();

        Assert.Equal(first, second);
    }

    [Fact]
    public void TopKLimitsTheNumberOfChunks()
    {
        RetrievalTrace trace = Retriever().Retrieve("refund order delivery return subscription", topK: 2);

        Assert.True(trace.Chunks.Count <= 2);
    }

    [Fact]
    public void TraceExposesContextInBothShapesTheTriadNeeds()
    {
        RetrievalTrace trace = Retriever().Retrieve("how long do refunds take");

        Assert.Equal(trace.Chunks.Count, trace.ChunkTexts.Count);
        Assert.Contains(trace.Chunks[0].Text, trace.Combined, StringComparison.Ordinal);
    }
}

