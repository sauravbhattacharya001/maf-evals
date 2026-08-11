using System.Text.Json.Serialization;

namespace EvalFramework.Cost;

/// <summary>Token and cost totals for one model's calls during a run.</summary>
public sealed record CostSummary
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    /// <summary>Calls that actually reached the provider. Cache hits are excluded.</summary>
    [JsonPropertyName("billedCalls")]
    public required int BilledCalls { get; init; }

    [JsonPropertyName("inputTokens")]
    public required long InputTokens { get; init; }

    [JsonPropertyName("outputTokens")]
    public required long OutputTokens { get; init; }

    [JsonPropertyName("totalTokens")]
    public required long TotalTokens { get; init; }

    /// <summary>Null when no price is configured for the model, rather than a misleading zero.</summary>
    [JsonPropertyName("estimatedCostUsd")]
    public double? EstimatedCostUsd { get; init; }

}

/// <summary>
/// Accumulates token usage across calls.
/// </summary>
/// <remarks>
/// Thread-safe because judging runs with bounded concurrency. Counts only what passes through it,
/// which is why the tracker is installed below the response cache: a cache hit costs nothing and
/// must not appear as spend, otherwise the claim that caching makes a judge affordable can never
/// be checked against reality.
/// </remarks>
public sealed class UsageTracker(string model)
{
    private readonly object _gate = new();

    private int _calls;
    private long _input;
    private long _output;
    private long _total;

    public string Model { get; } = model;

    public void Record(Microsoft.Extensions.AI.UsageDetails? usage)
    {
        if (usage is null)
        {
            return;
        }

        lock (_gate)
        {
            _calls++;
            _input += usage.InputTokenCount ?? 0;
            _output += usage.OutputTokenCount ?? 0;
            _total += usage.TotalTokenCount
                ?? (usage.InputTokenCount ?? 0) + (usage.OutputTokenCount ?? 0);
        }
    }

    public CostSummary Snapshot(ModelPricing? pricing = null)
    {
        lock (_gate)
        {
            return new CostSummary
            {
                Model = Model,
                BilledCalls = _calls,
                InputTokens = _input,
                OutputTokens = _output,
                TotalTokens = _total,
                EstimatedCostUsd = pricing?.Estimate(Model, _input, _output)
            };
        }
    }
}


