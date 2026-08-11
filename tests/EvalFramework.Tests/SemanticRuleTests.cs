using EvalFramework.Rules;
using Microsoft.Extensions.AI;

namespace EvalFramework.Tests;

/// <summary>
/// Semantic expectations, written after a keyword list failed three times on correct behaviour.
/// </summary>
/// <remarks>
/// The agent declined an over-limit refund and explained the cap three different ways: "without
/// escalation", "up to 500 units", "without additional approval". Each fix added the missing word
/// and the next run found another. These tests use a stub generator so the rule logic is verified
/// without a network call or a bill.
/// </remarks>
public sealed class SemanticRuleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SemanticExpectation Declines(double threshold = 0.55) => new()
    {
        Name = "declines_and_explains_limit",
        AnyOf = ["I cannot approve a refund of that size myself, it needs a supervisor."],
        MinSimilarity = threshold
    };

    [Fact]
    public async Task APhrasingCloseInMeaningPasses()
    {
        StubEmbeddingGenerator stub = new([1f, 0f], [0.95f, 0.31f]);

        RuleReport report = await new SemanticRuleEvaluator(stub)
            .EvaluateAsync([Declines()], "response", Ct);

        Assert.True(report.Passed);
    }

    [Fact]
    public async Task AnUnrelatedAnswerFails()
    {
        StubEmbeddingGenerator stub = new([1f, 0f], [0f, 1f]);

        RuleReport report = await new SemanticRuleEvaluator(stub)
            .EvaluateAsync([Declines()], "response", Ct);

        Assert.False(report.Passed);
        Assert.Contains("required", report.Failures[0].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheClosestOfSeveralReferencesIsUsed()
    {
        // Only one reference needs to match, which is what makes this tolerant of phrasing.
        SemanticExpectation expectation = new()
        {
            Name = "declines",
            AnyOf = ["far away", "very close"],
            MinSimilarity = 0.9
        };

        StubEmbeddingGenerator stub = new([1f, 0f], [0f, 1f], [0.99f, 0.14f]);

        RuleReport report = await new SemanticRuleEvaluator(stub)
            .EvaluateAsync([expectation], "response", Ct);

        Assert.True(report.Passed);
        Assert.Contains("reference 2", report.Checks[0].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmbeddingOutageDoesNotFailTheCase()
    {
        // Missing evidence must never be reported as an agent regression.
        RuleReport report = await new SemanticRuleEvaluator(new FailingEmbeddingGenerator())
            .EvaluateAsync([Declines()], "anything", Ct);

        Assert.True(report.Passed);
        Assert.Contains("not evaluated", report.Checks[0].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoExpectationsMeansNoChecks()
    {
        RuleReport report = await new SemanticRuleEvaluator(new FailingEmbeddingGenerator())
            .EvaluateAsync([], "anything", Ct);

        Assert.Empty(report.Checks);
    }

    [Theory]
    [InlineData(new float[] { 1f, 0f }, new float[] { 1f, 0f }, 1.0)]
    [InlineData(new float[] { 1f, 0f }, new float[] { 0f, 1f }, 0.0)]
    [InlineData(new float[] { 1f, 0f }, new float[] { -1f, 0f }, -1.0)]
    public void CosineSimilarityBehavesAsExpected(float[] left, float[] right, double expected)
    {
        Assert.Equal(expected, SemanticRuleEvaluator.CosineSimilarity(left, right), 6);
    }

    [Fact]
    public void MismatchedOrEmptyVectorsScoreZeroRatherThanThrowing()
    {
        Assert.Equal(0, SemanticRuleEvaluator.CosineSimilarity([1f, 0f], [1f]), 6);
        Assert.Equal(0, SemanticRuleEvaluator.CosineSimilarity([], []), 6);
    }

    /// <summary>Returns vectors positionally: the response first, then each reference in order.</summary>
    private sealed class StubEmbeddingGenerator(params float[][] vectors)
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            GeneratedEmbeddings<Embedding<float>> generated = [];
            int index = 0;

            foreach (string _ in values)
            {
                generated.Add(new Embedding<float>(
                    index < vectors.Length ? vectors[index] : [0f, 0f]));
                index++;
            }

            return Task.FromResult(generated);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class FailingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("503 Service Unavailable");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

