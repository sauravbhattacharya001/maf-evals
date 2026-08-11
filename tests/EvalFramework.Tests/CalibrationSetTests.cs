using EvalFramework.Calibration;
using EvalFramework.RagTriad;

namespace EvalFramework.Tests;

/// <summary>
/// Guards the labelled set itself. A calibration set that only contains easy, agreeable cases
/// measures nothing: it would validate any judge, including a broken one.
/// </summary>
public sealed class CalibrationSetTests
{
    private static IReadOnlyList<CalibrationCase> Cases()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "datasets")))
        {
            directory = directory.Parent;
        }

        return CalibrationSet.Load(
            Path.Combine(directory!.FullName, "datasets", "judge-calibration.jsonl"));
    }

    [Fact]
    public void SetLoadsAndEveryCaseIsDocumented()
    {
        IReadOnlyList<CalibrationCase> cases = Cases();

        Assert.NotEmpty(cases);
        Assert.All(cases, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Rationale));
            Assert.False(string.IsNullOrWhiteSpace(item.Label));
        });
    }

    [Fact]
    public void CaseIdsAreUnique()
    {
        string[] ids = Cases().Select(item => item.Id).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void LabelsStayOnTheOneToFiveScale()
    {
        foreach (CalibrationCase item in Cases())
        {
            foreach (string metric in new[]
                     { TriadMetrics.Retrieval, TriadMetrics.Groundedness, TriadMetrics.Relevance })
            {
                Assert.InRange(item.Labels.For(metric), 1, 5);
            }
        }
    }

    [Fact]
    public void EveryMetricSpansTheFullRange()
    {
        // Without both failing and passing examples per metric, agreement figures are meaningless.
        foreach (string metric in new[]
                 { TriadMetrics.Retrieval, TriadMetrics.Groundedness, TriadMetrics.Relevance })
        {
            double[] labels = Cases().Select(item => item.Labels.For(metric)).ToArray();

            Assert.True(labels.Min() <= 2, $"{metric} has no clearly failing example.");
            Assert.True(labels.Max() >= 5, $"{metric} has no clearly excellent example.");
        }
    }

    [Fact]
    public void TheSetContainsCasesWhereTheMetricsDisagreeWithEachOther()
    {
        // The point of three metrics is that they can diverge. A set where they always move
        // together cannot detect a judge that has collapsed them into one number.
        int divergent = Cases().Count(item =>
            Math.Abs(item.Labels.Groundedness - item.Labels.Relevance) >= 3
            || Math.Abs(item.Labels.Retrieval - item.Labels.Relevance) >= 3);

        Assert.True(divergent >= 3, $"Only {divergent} case(s) separate the metrics.");
    }

    [Fact]
    public void EveryCaseCarriesContextSoRetrievalAndGroundednessCanBeScored()
    {
        Assert.All(Cases(), item =>
        {
            Assert.NotEmpty(item.Context);
            Assert.NotEmpty(item.ToRetrievalTrace().Chunks);
        });
    }

    [Fact]
    public void RetrievalTraceRanksContextInTheOrderGiven()
    {
        CalibrationCase multi = Cases().First(item => item.Context.Count > 1);
        var chunks = multi.ToRetrievalTrace().Chunks;

        Assert.True(chunks[0].Score > chunks[1].Score);
        Assert.Equal(multi.Context[0].Id, chunks[0].Id);
    }
}
