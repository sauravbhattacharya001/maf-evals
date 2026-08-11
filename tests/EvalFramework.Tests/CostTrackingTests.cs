using EvalFramework.Cost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace EvalFramework.Tests;

/// <summary>
/// Cost accounting. Without it, "caching makes a judge affordable on every pull request" is an
/// assertion; with it the saving is a number that can be checked.
/// </summary>
public sealed class CostTrackingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ModelPricing Pricing = new()
    {
        Rates = new Dictionary<string, ModelRate>(StringComparer.OrdinalIgnoreCase)
        {
            ["priced-model"] = new(InputPerMillion: 1.0, OutputPerMillion: 2.0)
        }
    };

    [Fact]
    public void TokensAreAccumulatedAcrossCalls()
    {
        UsageTracker tracker = new("priced-model");

        tracker.Record(new UsageDetails { InputTokenCount = 100, OutputTokenCount = 20, TotalTokenCount = 120 });
        tracker.Record(new UsageDetails { InputTokenCount = 400, OutputTokenCount = 80, TotalTokenCount = 480 });

        CostSummary summary = tracker.Snapshot(Pricing);

        Assert.Equal(2, summary.BilledCalls);
        Assert.Equal(500, summary.InputTokens);
        Assert.Equal(100, summary.OutputTokens);
        Assert.Equal(600, summary.TotalTokens);
    }

    [Fact]
    public void CostFollowsTheConfiguredRate()
    {
        UsageTracker tracker = new("priced-model");
        tracker.Record(new UsageDetails { InputTokenCount = 1_000_000, OutputTokenCount = 1_000_000 });

        // 1M input at $1.00 plus 1M output at $2.00.
        Assert.Equal(3.0, tracker.Snapshot(Pricing).EstimatedCostUsd!.Value, 6);
    }

    [Fact]
    public void AnUnpricedModelReportsNullRatherThanZero()
    {
        // Zero would understate spend exactly when a new model is introduced.
        UsageTracker tracker = new("unknown-model");
        tracker.Record(new UsageDetails { InputTokenCount = 1_000, OutputTokenCount = 1_000 });

        Assert.Null(tracker.Snapshot(Pricing).EstimatedCostUsd);
    }

    [Fact]
    public void APartiallyPricedTotalIsUnknownRatherThanMisleading()
    {
        CostSummary priced = new()
        {
            Model = "a", BilledCalls = 1, InputTokens = 1, OutputTokens = 1,
            TotalTokens = 2, EstimatedCostUsd = 0.5
        };

        CostSummary unpriced = priced with { Model = "b", EstimatedCostUsd = null };

        Assert.Equal(0.5, ModelPricing.Total(priced));
        Assert.Null(ModelPricing.Total(priced, unpriced));
    }

    [Fact]
    public void MissingUsageIsIgnoredRatherThanCountedAsACall()
    {
        UsageTracker tracker = new("priced-model");
        tracker.Record(null);

        Assert.Equal(0, tracker.Snapshot(Pricing).BilledCalls);
    }

    [Fact]
    public async Task CachedResponsesAreNotCountedAsSpend()
    {
        // The tracker sits below the cache, so a repeat call bills nothing. This is the property
        // that makes the caching claim verifiable rather than rhetorical.
        UsageTracker tracker = new("priced-model");
        CountingChatClient inner = new();

        IDistributedCache cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

        IChatClient client = inner
            .AsBuilder()
            .UseDistributedCache(cache)
            .UseUsageTracking(tracker)
            .Build();

        ChatMessage[] messages = [new(ChatRole.User, "same question")];

        await client.GetResponseAsync(messages, cancellationToken: Ct);
        await client.GetResponseAsync(messages, cancellationToken: Ct);

        Assert.Equal(1, inner.Calls);
        Assert.Equal(1, tracker.Snapshot(Pricing).BilledCalls);
    }

    [Fact]
    public async Task DistinctPromptsAreBilledSeparately()
    {
        UsageTracker tracker = new("priced-model");
        CountingChatClient inner = new();

        IDistributedCache cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        IChatClient client = inner.AsBuilder().UseDistributedCache(cache).UseUsageTracking(tracker).Build();

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "first")], cancellationToken: Ct);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "second")], cancellationToken: Ct);

        Assert.Equal(2, tracker.Snapshot(Pricing).BilledCalls);
    }

    [Fact]
    public void SnapshotIsStableWhileMoreCallsArrive()
    {
        UsageTracker tracker = new("priced-model");
        tracker.Record(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 10 });

        CostSummary first = tracker.Snapshot(Pricing);
        tracker.Record(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 10 });

        Assert.Equal(1, first.BilledCalls);
        Assert.Equal(2, tracker.Snapshot(Pricing).BilledCalls);
    }

    private sealed class CountingChatClient : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer"))
            {
                Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5, TotalTokenCount = 15 }
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
