using EvalFramework.Execution;
using EvalFramework.Rules;
using EvalFramework.Trajectory;
using Microsoft.Extensions.AI;

namespace EvalFramework.Tests;

/// <summary>
/// The Tier 3 evaluator itself, driven by a stub judge so the wiring is verified without spend.
/// </summary>
/// <remarks>
/// Two defects lived here and both were invisible until a live run: tool calls were passed outside
/// the response the evaluator inspects, and a boolean metric was discarded because only numeric
/// metrics were unwrapped. Both produced a plausible looking result rather than an error, which is
/// the failure mode these tests exist to prevent.
/// </remarks>
public sealed class TrajectoryEvaluatorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly AITool[] Tools =
    [
        AIFunctionFactory.Create((string orderId) => "ok", "lookup_order")
    ];

    private static ResponseRecord Record(bool withToolCall)
    {
        ChatMessage[] messages = withToolCall
            ?
            [
                new(ChatRole.Assistant, [new FunctionCallContent("c1", "lookup_order", new Dictionary<string, object?> { ["orderId"] = "A-1" })]),
                new(ChatRole.Tool, [new FunctionResultContent("c1", "Order A-1: dispatched.")]),
                new(ChatRole.Assistant, "Your order is dispatched.")
            ]
            : [new ChatMessage(ChatRole.Assistant, "Open Account, then Subscriptions.")];

        return new ResponseRecord
        {
            CaseId = "case",
            Query = "Where is order A-1?",
            Repetition = 1,
            Response = messages[^1].Text ?? string.Empty,
            LatencyMs = 1,
            Rules = new RuleReport([]),
            Trajectory = Execution.Trajectory.Capture(messages),
            ToolCalls = withToolCall
                ? [new ToolCallRecord("lookup_order", new Dictionary<string, object?> { ["orderId"] = "A-1" })]
                : []
        };
    }

    [Fact]
    public async Task ToolCallAccuracyIsAttemptedOnlyWhenAToolWasCalled()
    {
        TrajectoryEvaluator evaluator = new(new ScriptedJudge("1"), Tools);

        TrajectoryResult withTool = await evaluator.EvaluateAsync(Record(true), cancellationToken: Ct);
        TrajectoryResult withoutTool = await evaluator.EvaluateAsync(Record(false), cancellationToken: Ct);

        Assert.Contains(withTool.Scores, score => score.Metric == TrajectoryMetrics.ToolCallAccuracy);
        Assert.DoesNotContain(withoutTool.Scores, score => score.Metric == TrajectoryMetrics.ToolCallAccuracy);
    }

    [Fact]
    public async Task ABooleanToolCallVerdictIsReadAsAScore()
    {
        // The defect this pins: tool call accuracy returns a BooleanMetric, and unwrapping only
        // numeric metrics silently dropped every score while keeping the judge's explanation.
        TrajectoryResult result = await new TrajectoryEvaluator(new ScriptedJudge("1"), Tools)
            .EvaluateAsync(Record(true), cancellationToken: Ct);

        TrajectoryScore tools = result.Scores.Single(s => s.Metric == TrajectoryMetrics.ToolCallAccuracy);

        Assert.Equal(1d, tools.Score);
    }

    [Fact]
    public async Task ARejectedToolCallVerdictScoresZeroRatherThanNothing()
    {
        TrajectoryResult result = await new TrajectoryEvaluator(new ScriptedJudge("0"), Tools)
            .EvaluateAsync(Record(true), cancellationToken: Ct);

        TrajectoryScore tools = result.Scores.Single(s => s.Metric == TrajectoryMetrics.ToolCallAccuracy);

        Assert.Equal(0d, tools.Score);
    }

    [Fact]
    public async Task TheToolCallReachesTheEvaluatorInTheResponseItInspects()
    {
        // Passing only the closing text as the response hid the calls in earlier turns, and the
        // evaluator reported that it had been given none.
        ScriptedJudge judge = new("1");

        await new TrajectoryEvaluator(judge, Tools).EvaluateAsync(Record(true), cancellationToken: Ct);

        Assert.Contains("lookup_order", judge.LastPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AJudgeOutageCostsOneMetricNotTheRun()
    {
        TrajectoryResult result = await new TrajectoryEvaluator(new FailingJudge(), Tools)
            .EvaluateAsync(Record(true), cancellationToken: Ct);

        Assert.NotEmpty(result.Scores);
        Assert.All(result.Scores, score => Assert.Null(score.Score));
        Assert.All(result.Scores, score =>
            Assert.Contains("judge call failed", score.Reason!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task EveryMetricIsStillNamedWhenTheJudgeIsDown()
    {
        TrajectoryResult result = await new TrajectoryEvaluator(new FailingJudge(), Tools)
            .EvaluateAsync(Record(true), cancellationToken: Ct);

        string[] metrics = result.Scores.Select(score => score.Metric).ToArray();

        Assert.Contains(TrajectoryMetrics.IntentResolution, metrics);
        Assert.Contains(TrajectoryMetrics.TaskAdherence, metrics);
        Assert.Contains(TrajectoryMetrics.ToolCallAccuracy, metrics);
    }

    [Fact]
    public async Task ManyTrajectoriesComeBackInInputOrder()
    {
        ResponseRecord[] records = Enumerable.Range(1, 5)
            .Select(i => Record(false) with { CaseId = $"case-{i}" })
            .ToArray();

        IReadOnlyList<TrajectoryResult> results = await new TrajectoryEvaluator(new FailingJudge(), Tools)
            .EvaluateManyAsync(records, maxConcurrency: 3, cancellationToken: Ct);

        Assert.Equal(records.Select(r => r.CaseId), results.Select(r => r.CaseId));
    }

    /// <summary>Answers in the tagged format the quality evaluators parse.</summary>
    private sealed class ScriptedJudge(string score) : IChatClient
    {
        public string LastPrompt { get; private set; } = string.Empty;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastPrompt = string.Join("\n", messages.Select(message => message.Text));

            return Task.FromResult(new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                $"<S0>Let's think step by step: fine.</S0><S1>Looks correct.</S1><S2>{score}</S2>")));
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

    private sealed class FailingJudge : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("503 Service Unavailable");

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
