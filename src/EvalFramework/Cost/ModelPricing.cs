using System.Text.Json.Serialization;

namespace EvalFramework.Cost;

/// <summary>Price per million tokens for one model.</summary>
public sealed record ModelRate(
    [property: JsonPropertyName("inputPerMillion")] double InputPerMillion,
    [property: JsonPropertyName("outputPerMillion")] double OutputPerMillion);

/// <summary>
/// Model prices, configured rather than hardcoded.
/// </summary>
/// <remarks>
/// Prices change and vary by region and contract, so an unknown model yields <see langword="null"/>
/// rather than zero. Reporting a confident zero for an unpriced model would understate spend
/// exactly when a new model is introduced, which is when the number matters most.
/// </remarks>
public sealed class ModelPricing
{
    [JsonPropertyName("rates")]
    public IReadOnlyDictionary<string, ModelRate> Rates { get; init; } =
        new Dictionary<string, ModelRate>(StringComparer.OrdinalIgnoreCase);

    public double? Estimate(string model, long inputTokens, long outputTokens)
    {
        if (!Rates.TryGetValue(model, out ModelRate? rate))
        {
            return null;
        }

        return ((inputTokens / 1_000_000d) * rate.InputPerMillion)
            + ((outputTokens / 1_000_000d) * rate.OutputPerMillion);
    }

    /// <summary>Sums costs, returning null if any component is unpriced.</summary>
    public static double? Total(params CostSummary?[] summaries)
    {
        CostSummary[] present = summaries.Where(summary => summary is not null).Cast<CostSummary>().ToArray();

        if (present.Length == 0)
        {
            return null;
        }

        // A partially known total is worse than an unknown one: it looks authoritative.
        return present.Any(summary => summary.EstimatedCostUsd is null)
            ? null
            : present.Sum(summary => summary.EstimatedCostUsd!.Value);
    }
}
