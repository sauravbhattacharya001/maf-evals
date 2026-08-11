using EvalFramework.Datasets;
using EvalFramework.Execution;
using EvalFramework.RagTriad;
using EvalFramework.Retrieval;
using EvalFramework.Rules;
using Microsoft.Extensions.AI;

namespace EvalFramework.Tests;

/// <summary>
/// A judge outage must cost one metric, not the whole run. Discarding results already paid for
/// and retrying from scratch would bill the candidate model twice for the same evidence.
/// </summary>
public sealed class TriadFailureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly GoldenCase Case = new()
    {
        Id = "case",
        Query = "How do I get a refund?",
        MinLength = 1,
        RequireActionableFormat = false
    };

    private static ResponseRecord Record(bool withContext) => new()
    {
        CaseId = Case.Id,
        Query = Case.Query,
        Repetition = 1,
        Response = "Contact support with your order number.",
        LatencyMs = 5,
        Rules = ResponseRules.Evaluate(Case.ToRuleSet(), "Contact support with your order number."),
        Retrieval = withContext
            ? new RetrievalTrace(Case.Query, [new RetrievedChunk("refunds#1", "Refunds", "policy text", 1)])
            : null
    };

    [Fact]
    public async Task JudgeOutageProducesNotScoredInsteadOfThrowing()
    {
        TriadEvaluator evaluator = new(new FailingChatClient());

        TriadResult result = await evaluator.EvaluateAsync(Record(withContext: true), cancellationToken: Ct);

        Assert.NotEmpty(result.Scores);
        Assert.All(result.Scores, score => Assert.Equal(TriadVerdict.NotScored, score.Verdict));
    }

    [Fact]
    public async Task EveryTriadMetricIsStillNamedWhenTheJudgeIsDown()
    {
        TriadResult result = await new TriadEvaluator(new FailingChatClient())
            .EvaluateAsync(Record(withContext: true), cancellationToken: Ct);

        string[] metrics = result.Scores.Select(score => score.Metric).ToArray();

        Assert.Contains(TriadMetrics.Retrieval, metrics);
        Assert.Contains(TriadMetrics.Groundedness, metrics);
        Assert.Contains(TriadMetrics.Relevance, metrics);
    }

    [Fact]
    public async Task TheFailureReasonIsRecordedForDiagnosis()
    {
        TriadResult result = await new TriadEvaluator(new FailingChatClient())
            .EvaluateAsync(Record(withContext: false), cancellationToken: Ct);

        Assert.Contains(result.Scores, score =>
            score.Reason is not null && score.Reason.Contains("judge call failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotScoredIsNotTreatedAsAFailingScore()
    {
        TriadResult result = await new TriadEvaluator(new FailingChatClient())
            .EvaluateAsync(Record(withContext: true), cancellationToken: Ct);

        // It is an absence of evidence, so it must not silently block a merge either.
        Assert.False(result.Failed);
    }

    private sealed class FailingChatClient : IChatClient
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
            throw new HttpRequestException("503 Service Unavailable");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
