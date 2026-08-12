using System.Text.Json.Serialization;
using EvalFramework.Datasets;
using EvalFramework.Rules;

namespace EvalFramework.Calibration;

/// <summary>How well a similarity threshold can separate good answers from bad ones.</summary>
public sealed record SemanticSeparation
{
    [JsonPropertyName("caseId")]
    public required string CaseId { get; init; }

    [JsonPropertyName("expectation")]
    public required string Expectation { get; init; }

    [JsonPropertyName("currentThreshold")]
    public required double CurrentThreshold { get; init; }

    /// <summary>Worst score among answers that must pass.</summary>
    [JsonPropertyName("minAccepted")]
    public required double MinAccepted { get; init; }

    /// <summary>Best score among answers that must fail.</summary>
    [JsonPropertyName("maxRejected")]
    public required double MaxRejected { get; init; }

    [JsonPropertyName("accepted")]
    public required int Accepted { get; init; }

    [JsonPropertyName("rejected")]
    public required int Rejected { get; init; }

    /// <summary>True when some threshold puts every good answer above every bad one.</summary>
    [JsonIgnore]
    public bool Separable => MinAccepted > MaxRejected;

    /// <summary>Distance between the worst good answer and the best bad one.</summary>
    [JsonIgnore]
    public double Margin => MinAccepted - MaxRejected;

    /// <summary>
    /// Midway between the two groups, or null when they overlap.
    /// </summary>
    /// <remarks>
    /// A midpoint keeps the most room on both sides. When the groups overlap no threshold works,
    /// and the reference statements need to change rather than the number.
    /// </remarks>
    [JsonIgnore]
    public double? SuggestedThreshold =>
        Separable ? Math.Round((MinAccepted + MaxRejected) / 2, 2) : null;

    [JsonIgnore]
    public bool CurrentThresholdWorks => Separable
        && CurrentThreshold > MaxRejected
        && CurrentThreshold <= MinAccepted;
}

/// <summary>
/// Chooses semantic thresholds from labelled examples instead of by inspection.
/// </summary>
/// <remarks>
/// The first threshold was a guess, and it rejected a correct refusal at 0.49 against a guessed
/// 0.55. The repository already holds the labels needed to do better: the positive fixtures are
/// answers that must pass, and the negative fixtures are answers that must fail.
/// </remarks>
public static class SemanticCalibration
{
    public static async Task<IReadOnlyList<SemanticSeparation>> MeasureAsync(
        IReadOnlyList<GoldenCase> cases,
        PositiveFixtureSet positives,
        NegativeFixtureSet negatives,
        SemanticRuleEvaluator evaluator,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(evaluator);

        List<SemanticSeparation> results = [];

        foreach (GoldenCase goldenCase in cases.Where(item => item.SemanticExpectations.Count > 0))
        {
            string[] accepted = positives.Fixtures
                .Where(fixture => fixture.CaseId.Equals(goldenCase.Id, StringComparison.OrdinalIgnoreCase))
                .Select(fixture => fixture.Response)
                .ToArray();

            string[] rejected = negatives.Fixtures
                .Where(fixture => fixture.CaseId.Equals(goldenCase.Id, StringComparison.OrdinalIgnoreCase))
                .Select(fixture => fixture.Response)
                .ToArray();

            foreach (SemanticExpectation expectation in goldenCase.SemanticExpectations)
            {
                double[] acceptedScores = await ScoreAllAsync(
                    evaluator, expectation, accepted, cancellationToken).ConfigureAwait(false);

                double[] rejectedScores = await ScoreAllAsync(
                    evaluator, expectation, rejected, cancellationToken).ConfigureAwait(false);

                results.Add(new SemanticSeparation
                {
                    CaseId = goldenCase.Id,
                    Expectation = expectation.Name,
                    CurrentThreshold = expectation.MinSimilarity,
                    MinAccepted = acceptedScores.Length == 0 ? 0 : acceptedScores.Min(),
                    MaxRejected = rejectedScores.Length == 0 ? 0 : rejectedScores.Max(),
                    Accepted = acceptedScores.Length,
                    Rejected = rejectedScores.Length
                });

                progress?.Report($"measured {goldenCase.Id}/{expectation.Name}");
            }
        }

        return results;
    }

    private static async Task<double[]> ScoreAllAsync(
        SemanticRuleEvaluator evaluator,
        SemanticExpectation expectation,
        IReadOnlyList<string> responses,
        CancellationToken cancellationToken)
    {
        double[] scores = new double[responses.Count];

        for (int i = 0; i < responses.Count; i++)
        {
            scores[i] = await evaluator
                .BestSimilarityAsync(expectation, responses[i], cancellationToken)
                .ConfigureAwait(false);
        }

        return scores;
    }
}
