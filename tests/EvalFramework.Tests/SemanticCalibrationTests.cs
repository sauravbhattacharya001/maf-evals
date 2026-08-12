using EvalFramework.Calibration;
using EvalFramework.Datasets;
using EvalFramework.Rules;
using Microsoft.Extensions.AI;

namespace EvalFramework.Tests;

/// <summary>
/// Choosing a similarity threshold from labelled examples rather than by inspection.
/// </summary>
/// <remarks>
/// The first threshold was a guess. Measuring it showed two separate problems: it sat below the best
/// bad answer, so it would have accepted them, and the reference statements matched a hand-written
/// fixture rather than anything the agent actually says.
/// </remarks>
public sealed class SemanticCalibrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static GoldenCase Case(double threshold = 0.5) => new()
    {
        Id = "case",
        Query = "q",
        SemanticExpectations =
        [
            new SemanticExpectation
            {
                Name = "declines",
                AnyOf = ["reference"],
                MinSimilarity = threshold
            }
        ]
    };

    private static PositiveFixtureSet Positives(params string[] responses) => new()
    {
        Fixtures = responses.Select(response => new PositiveFixture
        {
            CaseId = "case",
            Response = response
        }).ToArray()
    };

    private static NegativeFixtureSet Negatives(params string[] responses) => new()
    {
        Fixtures = responses.Select(response => new NegativeFixture
        {
            CaseId = "case",
            Label = "bad",
            Response = response,
            ExpectedFailures = ["declines"]
        }).ToArray()
    };

    private static Task<IReadOnlyList<SemanticSeparation>> MeasureAsync(
        GoldenCase goldenCase,
        PositiveFixtureSet positives,
        NegativeFixtureSet negatives,
        StubEmbeddings embeddings) =>
        SemanticCalibration.MeasureAsync(
            [goldenCase], positives, negatives, new SemanticRuleEvaluator(embeddings),
            cancellationToken: Ct);

    [Fact]
    public async Task SeparableExamplesGiveAThresholdBetweenThem()
    {
        StubEmbeddings embeddings = new()
        {
            ["reference"] = [1f, 0f],
            ["good"] = [0.9f, 0.44f],   // about 0.90
            ["bad"] = [0.5f, 0.87f]     // about 0.50
        };

        SemanticSeparation result = (await MeasureAsync(
            Case(), Positives("good"), Negatives("bad"), embeddings)).Single();

        Assert.True(result.Separable);
        Assert.InRange(result.SuggestedThreshold!.Value, 0.6, 0.8);
    }

    [Fact]
    public async Task OverlappingExamplesGiveNoThresholdAtAll()
    {
        // A good and a bad answer that mean nearly the same thing cannot be told apart this way.
        // The honest output is to say so, rather than to suggest a number that cannot work.
        StubEmbeddings embeddings = new()
        {
            ["reference"] = [1f, 0f],
            ["good"] = [0.5f, 0.87f],
            ["bad"] = [0.9f, 0.44f]
        };

        SemanticSeparation result = (await MeasureAsync(
            Case(), Positives("good"), Negatives("bad"), embeddings)).Single();

        Assert.False(result.Separable);
        Assert.Null(result.SuggestedThreshold);
        Assert.True(result.Margin < 0);
    }

    [Fact]
    public async Task AThresholdBelowTheBestBadAnswerIsReportedAsWrong()
    {
        // The failure that started this: 0.55 sat under bad answers scoring 0.61.
        StubEmbeddings embeddings = new()
        {
            ["reference"] = [1f, 0f],
            ["good"] = [0.99f, 0.14f],
            ["bad"] = [0.8f, 0.6f]
        };

        SemanticSeparation result = (await MeasureAsync(
            Case(threshold: 0.1), Positives("good"), Negatives("bad"), embeddings)).Single();

        Assert.True(result.Separable);
        Assert.False(result.CurrentThresholdWorks);
    }

    [Fact]
    public async Task AWorkingThresholdIsLeftAlone()
    {
        StubEmbeddings embeddings = new()
        {
            ["reference"] = [1f, 0f],
            ["good"] = [0.99f, 0.14f],
            ["bad"] = [0.5f, 0.87f]
        };

        SemanticSeparation result = (await MeasureAsync(
            Case(threshold: 0.75), Positives("good"), Negatives("bad"), embeddings)).Single();

        Assert.True(result.CurrentThresholdWorks);
    }

    [Fact]
    public async Task EveryLabelledAnswerCounts()
    {
        // The worst good answer and the best bad one set the boundary, not the averages.
        StubEmbeddings embeddings = new()
        {
            ["reference"] = [1f, 0f],
            ["good"] = [0.99f, 0.14f],
            ["also good"] = [0.8f, 0.6f],
            ["bad"] = [0.3f, 0.95f]
        };

        SemanticSeparation result = (await MeasureAsync(
            Case(), Positives("good", "also good"), Negatives("bad"), embeddings)).Single();

        Assert.Equal(2, result.Accepted);
        Assert.InRange(result.MinAccepted, 0.75, 0.85);
    }

    [Fact]
    public async Task CasesWithoutSemanticExpectationsAreSkipped()
    {
        GoldenCase plain = new() { Id = "case", Query = "q" };

        IReadOnlyList<SemanticSeparation> results = await MeasureAsync(
            plain, Positives("good"), Negatives("bad"), new StubEmbeddings());

        Assert.Empty(results);
    }

    private sealed class StubEmbeddings : Dictionary<string, float[]>,
        IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            GeneratedEmbeddings<Embedding<float>> generated = [];

            foreach (string value in values)
            {
                generated.Add(new Embedding<float>(TryGetValue(value, out float[]? vector) ? vector : [0f, 1f]));
            }

            return Task.FromResult(generated);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
