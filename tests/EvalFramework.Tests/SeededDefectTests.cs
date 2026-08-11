using EvalFramework.Datasets;
using EvalFramework.Execution;
using EvalFramework.RagTriad;
using EvalFramework.Statistics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SupportAgent;
using SupportAgent.Retrieval;

namespace EvalFramework.Tests;

/// <summary>
/// Seeds deliberate defects and requires the gate to catch them.
/// </summary>
/// <remarks>
/// Unit tests of <see cref="Tier2Gate"/> prove the gate logic given hand-built records. They cannot
/// prove the pipeline is wired: an agent whose guardrails were never attached, or a runner that
/// dropped the retrieval trace, would still pass them. These tests drive a real agent, real
/// guardrails, a real retriever, and the real runner, then assert the gate reacts. Everything runs
/// offline against a scripted chat client.
/// </remarks>
public sealed class SeededDefectTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "corpus")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("corpus not found.");
    }

    private static IReadOnlyList<GoldenCase> Cases() =>
        GoldenSet.Load(Path.Combine(RepoRoot(), "datasets", "support-golden-set.jsonl"))
            .Where(item => item.Id is "double-charge" or "damaged-return")
            .ToArray();

    private static KeywordRetriever Retriever() =>
        KeywordRetriever.FromDirectory(Path.Combine(RepoRoot(), "corpus"));

    private static async Task<Tier2Result> RunGateAsync(IChatClient client, IRetriever? retriever = null)
    {
        IReadOnlyList<GoldenCase> cases = Cases();
        (AIAgent agent, GuardrailRecorder recorder) =
            SupportAgentFactory.Create(client, retriever ?? Retriever());

        RunArtifact run = await new AgentRunner(agent, "seeded", new RecorderTelemetrySource(recorder))
            .RunAsync(cases, 1, "tier2", "seeded.jsonl", cancellationToken: Ct);

        return Tier2Gate.Apply(run, cases, [], new TriadThresholds());
    }

    /// <summary>The control: without it, a gate that always fails would look like success.</summary>
    [Fact]
    public async Task AHealthyAgentPassesTheGate()
    {
        ScriptedChatClient client = new(
            "1. Send support your order number.\n2. Include both transaction ids and we will refund the duplicate charge.",
            "1. Photograph the damage.\n2. Open a return request in your account within 30 days.");

        Tier2Result result = await RunGateAsync(client);

        Assert.True(result.Passed, string.Join("; ", result.Violations.Select(v => v.Detail)));
    }

    [Fact]
    public async Task AnAgentThatRefusesToHelpIsCaught()
    {
        Tier2Result result = await RunGateAsync(new ScriptedChatClient("I can't help with that."));

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, violation => violation.Gate == "rule");
    }

    [Fact]
    public async Task AnAgentThatDropsRequiredInformationIsCaught()
    {
        // Well formatted and plausible, but never asks for the order number.
        Tier2Result result = await RunGateAsync(new ScriptedChatClient(
            "1. Check your bank statement.\n2. Wait five business days for the pending charge to clear."));

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, violation => violation.Detail.Contains("expected_terms", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABrokenRetrieverIsCaughtEvenWhenTheAnswerLooksFine()
    {
        // The answers satisfy every rule; only the retrieval expectation exposes the defect.
        ScriptedChatClient client = new(
            "1. Send support your order number.\n2. Include both transaction ids and we will refund the duplicate charge.",
            "1. Photograph the damage.\n2. Open a return request in your account within 30 days.");

        Tier2Result result = await RunGateAsync(client, new EmptyRetriever());

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, violation => violation.Gate == "retrieval");
    }

    [Fact]
    public async Task AnApiOutageIsReportedAsInfrastructureNotAsAgentFailure()
    {
        Tier2Result result = await RunGateAsync(new FailingChatClient());

        Assert.False(result.Passed);
        Assert.All(result.Violations, violation => Assert.Equal("infrastructure", violation.Gate));
        Assert.Equal(result.Run.Responses.Count, result.Run.ErroredCount);

        // Crucially, the pass rate must not be reported as a collapse to zero.
        Assert.Equal(0, result.Run.Cases.Sum(statistics => statistics.Trials));
    }

    [Fact]
    public async Task UnsafeContentIsBlockedByTier1AndSurfacedByTier2()
    {
        Tier2Result result = await RunGateAsync(new ScriptedChatClient(
            "You should double your dose while you wait."));

        Assert.Contains(result.Violations, violation => violation.Gate == "blocked");
    }

    [Fact]
    public async Task GuardrailRetriesAreVisibleInTheArtifact()
    {
        // First answer is too short, second is acceptable: the run should record two attempts.
        ScriptedChatClient client = new(
            "No.",
            "1. Send support your order number.\n2. Include both transaction ids and we will refund the duplicate charge.");

        IReadOnlyList<GoldenCase> cases = Cases().Take(1).ToArray();
        (AIAgent agent, GuardrailRecorder recorder) = SupportAgentFactory.Create(client, Retriever());

        RunArtifact run = await new AgentRunner(agent, "seeded", new RecorderTelemetrySource(recorder))
            .RunAsync(cases, 1, "tier2", "seeded.jsonl", cancellationToken: Ct);

        Assert.Equal(2, run.Responses[0].Attempts);
    }

    private sealed class ScriptedChatClient(params string[] answers) : IChatClient
    {
        private int _index;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            string answer = answers[Math.Min(_index++, answers.Length - 1)];
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)));
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

    private sealed class FailingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("429 Too Many Requests");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("429 Too Many Requests");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class EmptyRetriever : IRetriever
    {
        public EvalFramework.Retrieval.RetrievalTrace Retrieve(string query, int topK = 3) =>
            EvalFramework.Retrieval.RetrievalTrace.Empty(query);
    }
}
