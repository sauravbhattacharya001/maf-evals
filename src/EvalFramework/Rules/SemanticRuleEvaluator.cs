using Microsoft.Extensions.AI;

namespace EvalFramework.Rules;

/// <summary>Checks semantic expectations by comparing embeddings of meaning, not of wording.</summary>
public sealed class SemanticRuleEvaluator(IEmbeddingGenerator<string, Embedding<float>> embeddings)
{
    public async Task<RuleReport> EvaluateAsync(
        IReadOnlyList<SemanticExpectation> expectations,
        string response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectations);

        if (expectations.Count == 0)
        {
            return new RuleReport([]);
        }

        List<CheckResult> checks = [];

        foreach (SemanticExpectation expectation in expectations)
        {
            checks.Add(await EvaluateOneAsync(expectation, response ?? string.Empty, cancellationToken)
                .ConfigureAwait(false));
        }

        return new RuleReport(checks);
    }

    private async Task<CheckResult> EvaluateOneAsync(
        SemanticExpectation expectation,
        string response,
        CancellationToken cancellationToken)
    {
        try
        {
            string[] inputs = [response, .. expectation.AnyOf];

            GeneratedEmbeddings<Embedding<float>> generated =
                await embeddings.GenerateAsync(inputs, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

            ReadOnlyMemory<float> actual = generated[0].Vector;

            double best = 0;
            int bestIndex = 0;

            for (int i = 1; i < generated.Count; i++)
            {
                double similarity = CosineSimilarity(actual.Span, generated[i].Vector.Span);

                if (similarity > best)
                {
                    best = similarity;
                    bestIndex = i - 1;
                }
            }

            bool passed = best >= expectation.MinSimilarity;

            return new CheckResult(
                expectation.Name,
                passed,
                $"closest reference {bestIndex + 1} at {best:F2}, required {expectation.MinSimilarity:F2}",
                expectation.Severity);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // An embedding outage is missing evidence, not a failed expectation. Passing keeps a
            // provider problem from being reported as an agent regression, and the detail says so.
            return new CheckResult(
                expectation.Name,
                true,
                $"not evaluated: embedding call failed ({error.Message})",
                expectation.Severity);
        }
    }

    internal static double CosineSimilarity(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length != right.Length || left.Length == 0)
        {
            return 0;
        }

        double dot = 0;
        double leftMagnitude = 0;
        double rightMagnitude = 0;

        for (int i = 0; i < left.Length; i++)
        {
            dot += left[i] * right[i];
            leftMagnitude += left[i] * left[i];
            rightMagnitude += right[i] * right[i];
        }

        double denominator = Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude);

        return denominator == 0 ? 0 : dot / denominator;
    }
}
