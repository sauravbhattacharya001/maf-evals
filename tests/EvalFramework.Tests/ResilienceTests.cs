using System.Diagnostics;
using EvalFramework.Datasets;
using EvalFramework.Execution;
using EvalFramework.RagTriad;
using EvalFramework.Retrieval;
using EvalFramework.Rules;
using Microsoft.Extensions.AI;

namespace EvalFramework.Tests;

/// <summary>
/// Resilience of the evaluation run itself: a hung provider must not stall a scheduled run, and
/// judging many records must stay inside a concurrency budget.
/// </summary>
public sealed class ResilienceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ResponseRecord Record(string caseId) => new()
    {
        CaseId = caseId,
        Query = "a question",
        Repetition = 1,
        Response = "an answer",
        LatencyMs = 1,
        Rules = new RuleReport([]),
        Retrieval = new RetrievalTrace("a question", [new RetrievedChunk("c#1", "t", "text", 1)])
    };

    [Fact]
    public async Task JudgeResultsComeBackInInputOrderDespiteConcurrency()
    {
        // Artifacts must be stable regardless of which call finishes first.
        ResponseRecord[] records = Enumerable.Range(1, 8).Select(i => Record($"case-{i}")).ToArray();

        IReadOnlyList<TriadResult> results = await new TriadEvaluator(new SlowFailingChatClient(5))
            .EvaluateManyAsync(records, maxConcurrency: 4, cancellationToken: Ct);

        Assert.Equal(records.Select(r => r.CaseId), results.Select(r => r.CaseId));
    }

    [Fact]
    public async Task ConcurrencyLimitIsRespected()
    {
        SlowFailingChatClient client = new(20);
        ResponseRecord[] records = Enumerable.Range(1, 8).Select(i => Record($"case-{i}")).ToArray();

        await new TriadEvaluator(client).EvaluateManyAsync(records, maxConcurrency: 3, cancellationToken: Ct);

        Assert.True(client.PeakConcurrency <= 3, $"peak was {client.PeakConcurrency}");
    }

    [Fact]
    public async Task ConcurrentJudgingIsFasterThanSequential()
    {
        ResponseRecord[] records = Enumerable.Range(1, 8).Select(i => Record($"case-{i}")).ToArray();

        long start = Stopwatch.GetTimestamp();
        await new TriadEvaluator(new SlowFailingChatClient(40))
            .EvaluateManyAsync(records, maxConcurrency: 8, cancellationToken: Ct);
        TimeSpan parallel = Stopwatch.GetElapsedTime(start);

        start = Stopwatch.GetTimestamp();
        await new TriadEvaluator(new SlowFailingChatClient(40))
            .EvaluateManyAsync(records, maxConcurrency: 1, cancellationToken: Ct);
        TimeSpan sequential = Stopwatch.GetElapsedTime(start);

        Assert.True(parallel < sequential, $"parallel {parallel.TotalMilliseconds}ms vs {sequential.TotalMilliseconds}ms");
    }

    [Fact]
    public async Task AHungJudgeCallBecomesNotScoredRatherThanHangingForever()
    {
        TriadResult result = await new TriadEvaluator(
                new SlowFailingChatClient(5000), thresholds: null, perCallTimeout: TimeSpan.FromMilliseconds(150))
            .EvaluateAsync(Record("slow"), cancellationToken: Ct);

        Assert.All(result.Scores, score => Assert.Equal(TriadVerdict.NotScored, score.Verdict));
        Assert.Contains(result.Scores, score =>
            score.Reason is not null && score.Reason.Contains("timed out", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AHungAgentIsRecordedAsErroredNotAsAFailure()
    {
        GoldenCase goldenCase = new()
        {
            Id = "hangs",
            Query = "a question",
            MinLength = 1,
            RequireActionableFormat = false
        };

        AgentRunner runner = new(
            new HangingAgent(), "test-model", telemetry: null, perRunTimeout: TimeSpan.FromMilliseconds(150));

        RunArtifact run = await runner.RunAsync([goldenCase], 1, "tier2", "d.jsonl", cancellationToken: Ct);

        Assert.True(run.Responses[0].Errored);
        Assert.Contains("timed out", run.Responses[0].Error!, StringComparison.Ordinal);
        Assert.Equal(1, run.ErroredCount);
    }

    [Fact]
    public async Task CallerCancellationStillPropagates()
    {
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        AgentRunner runner = new(new HangingAgent(), "test-model");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(
                [new GoldenCase { Id = "c", Query = "q" }], 1, "tier2", "d.jsonl", cancellationToken: cts.Token));
    }

    [Fact]
    public async Task ConcurrencyMustBePositive()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new TriadEvaluator(new SlowFailingChatClient(1))
                .EvaluateManyAsync([], maxConcurrency: 0, cancellationToken: Ct));
    }

    /// <summary>Fails after a delay, so timing and concurrency can be observed without a model.</summary>
    private sealed class SlowFailingChatClient(int delayMs) : IChatClient
    {
        private int _current;

        public int PeakConcurrency { get; private set; }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            int now = Interlocked.Increment(ref _current);
            PeakConcurrency = Math.Max(PeakConcurrency, now);

            try
            {
                await Task.Delay(delayMs, cancellationToken);
                throw new HttpRequestException("503 Service Unavailable");
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
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

    private sealed class HangingAgent : Microsoft.Agents.AI.AIAgent
    {
        protected override async Task<Microsoft.Agents.AI.AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            Microsoft.Agents.AI.AgentSession? session = null,
            Microsoft.Agents.AI.AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        }

        protected override IAsyncEnumerable<Microsoft.Agents.AI.AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            Microsoft.Agents.AI.AgentSession? session = null,
            Microsoft.Agents.AI.AgentRunOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        // Sessions are never created by the runner, which invokes the agent statelessly.
        protected override ValueTask<Microsoft.Agents.AI.AgentSession> CreateSessionCoreAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        protected override ValueTask<System.Text.Json.JsonElement> SerializeSessionCoreAsync(
            Microsoft.Agents.AI.AgentSession session,
            System.Text.Json.JsonSerializerOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        protected override ValueTask<Microsoft.Agents.AI.AgentSession> DeserializeSessionCoreAsync(
            System.Text.Json.JsonElement serializedSession,
            System.Text.Json.JsonSerializerOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
