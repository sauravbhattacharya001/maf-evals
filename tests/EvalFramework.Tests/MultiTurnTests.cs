using EvalFramework.Datasets;
using EvalFramework.Execution;
using EvalFramework.Rules;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SupportAgent;
using SupportAgent.Retrieval;

namespace EvalFramework.Tests;

/// <summary>
/// Conversations that take more than one message.
/// </summary>
/// <remarks>
/// A single question does not resemble real support traffic. The faults that matter appear later:
/// the agent forgets an order number it already has, or asks again for something the customer
/// answered. A one-shot case cannot see either.
/// </remarks>
public sealed class MultiTurnTests
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

    private static KeywordRetriever Retriever() =>
        KeywordRetriever.FromDirectories(Path.Combine(RepoRoot(), "corpus"));

    /// <summary>
    /// A reply that satisfies the agent own baseline guardrails.
    /// </summary>
    /// <remarks>
    /// The agent applies its policy to every answer, so a two word reply is refused by Tier 1 and
    /// retried before it ever reaches the runner. Test replies have to be as long and as well formed
    /// as real ones, or the test measures the guard instead of the conversation.
    /// </remarks>
    private static string Reply(string marker) =>
        $"1. Thank you, I have noted this: {marker}.\n2. I will confirm the next step with you shortly.";

    private static GoldenCase Conversation(params string[] turns) => new()
    {
        Id = "conversation",
        Query = turns[0],
        Turns = turns,
        MinLength = 1,
        RequireActionableFormat = false
    };

    [Fact]
    public void ASingleTurnCaseStillUsesItsQuery()
    {
        GoldenCase single = new() { Id = "single", Query = "How do I cancel?" };

        Assert.Equal(["How do I cancel?"], single.EffectiveTurns);
        Assert.False(single.IsMultiTurn);
    }

    [Fact]
    public void AConversationUsesItsTurns()
    {
        GoldenCase many = Conversation("First message.", "Second message.");

        Assert.Equal(2, many.EffectiveTurns.Count);
        Assert.True(many.IsMultiTurn);
    }

    [Fact]
    public async Task EveryTurnIsSentInOrder()
    {
        ScriptedChatClient client = new(Reply("one"), Reply("two"), Reply("three"));
        (AIAgent agent, GuardrailRecorder recorder) = SupportAgentFactory.Create(client, Retriever());

        GoldenCase conversation = Conversation("First.", "Second.", "Third.");

        RunArtifact run = await new AgentRunner(agent, "test", new RecorderTelemetrySource(recorder))
            .RunAsync([conversation], 1, "tier2", "d.jsonl", cancellationToken: Ct);

        string[] asked = client.Requests
            .Select(request => request.Last(message => message.Role == ChatRole.User).Text!)
            .ToArray();

        Assert.Equal(["First.", "Second.", "Third."], asked);
        Assert.Contains("three", run.Responses[0].Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAgentRemembersEarlierTurns()
    {
        // The second request must carry the first exchange, or the agent cannot answer a follow-up.
        ScriptedChatClient client = new(Reply("which order"), Reply("checking A-1 now"));
        (AIAgent agent, GuardrailRecorder recorder) = SupportAgentFactory.Create(client, Retriever());

        await new AgentRunner(agent, "test", new RecorderTelemetrySource(recorder))
            .RunAsync([Conversation("Where is my order?", "It is A-1.")], 1, "tier2", "d.jsonl",
                cancellationToken: Ct);

        IReadOnlyList<ChatMessage> second = client.Requests[1];

        Assert.Contains(second, message =>
            message.Role == ChatRole.User && message.Text!.Contains("Where is my order?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheTrajectoryRecordsBothSidesOfTheConversation()
    {
        ScriptedChatClient client = new(Reply("first answer"), Reply("second answer"));
        (AIAgent agent, GuardrailRecorder recorder) = SupportAgentFactory.Create(client, Retriever());

        RunArtifact run = await new AgentRunner(agent, "test", new RecorderTelemetrySource(recorder))
            .RunAsync([Conversation("First question.", "Second question.")], 1, "tier2", "d.jsonl",
                cancellationToken: Ct);

        IReadOnlyList<TrajectoryMessage> trajectory = run.Responses[0].Trajectory;

        Assert.Equal(2, trajectory.Count(turn => turn.Role == "user"));
        Assert.Contains(trajectory, turn => turn.Text.Contains("Second question.", StringComparison.Ordinal));
        Assert.Contains(trajectory, turn => turn.Text.Contains("second answer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheRulesJudgeTheFinalAnswer()
    {
        // An early turn may legitimately ask a question. Only the last answer resolves the request.
        ScriptedChatClient client = new(Reply("which order number"), Reply("refunded order A-1 for you"));

        GoldenCase conversation = new()
        {
            Id = "conversation",
            Query = "Refund me.",
            Turns = ["Refund me.", "Order A-1."],
            MinLength = 1,
            RequireActionableFormat = false,
            ExpectedTerms = ["A-1"]
        };

        (AIAgent agent, GuardrailRecorder recorder) = SupportAgentFactory.Create(client, Retriever());

        RunArtifact run = await new AgentRunner(agent, "test", new RecorderTelemetrySource(recorder))
            .RunAsync([conversation], 1, "tier2", "d.jsonl", cancellationToken: Ct);

        Assert.True(run.Responses[0].Rules.Passed);
    }

    [Fact]
    public async Task AnAgentThatForgetsTheOrderNumberIsCaught()
    {
        ScriptedChatClient client = new(Reply("which order"), Reply("which order was that again"));

        GoldenCase conversation = new()
        {
            Id = "conversation",
            Query = "Refund me.",
            Turns = ["Refund me.", "Order A-1."],
            MinLength = 1,
            RequireActionableFormat = false,
            ExpectedTerms = ["A-1"]
        };

        (AIAgent agent, GuardrailRecorder recorder) = SupportAgentFactory.Create(client, Retriever());

        RunArtifact run = await new AgentRunner(agent, "test", new RecorderTelemetrySource(recorder))
            .RunAsync([conversation], 1, "tier2", "d.jsonl", cancellationToken: Ct);

        Assert.False(run.Responses[0].Rules.Passed);
    }

    [Fact]
    public async Task ToolCallsFromAnyTurnAreRecorded()
    {
        // The agent usually cannot act until a later turn supplies the order number.
        ScriptedChatClient client = new(
            Reply("which order"),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(
                "c1", SupportPolicy.LookupOrderTool, new Dictionary<string, object?> { ["orderId"] = "A-1" })]),
            Reply("order A-1 is dispatched"));

        (AIAgent agent, GuardrailRecorder recorder) = SupportAgentFactory.Create(client, Retriever());

        RunArtifact run = await new AgentRunner(agent, "test", new RecorderTelemetrySource(recorder))
            .RunAsync([Conversation("Where is my order?", "It is A-1.")], 1, "tier2", "d.jsonl",
                cancellationToken: Ct);

        Assert.Contains(run.Responses[0].ToolCalls, call => call.Name == SupportPolicy.LookupOrderTool);
    }

    [Fact]
    public async Task AGuardRetryInsideAConversationSendsOnlyTheCorrection()
    {
        // With a session the agent already remembers the exchange, so replaying the whole
        // conversation would duplicate it. This path had no coverage until conversations existed.
        ScriptedChatClient client = new("No.", Reply("corrected"), Reply("second turn"));
        (AIAgent agent, GuardrailRecorder recorder) = SupportAgentFactory.Create(client, Retriever());

        await new AgentRunner(agent, "test", new RecorderTelemetrySource(recorder))
            .RunAsync([Conversation("First question.", "Second question.")], 1, "tier2", "d.jsonl",
                cancellationToken: Ct);

        // Request 1 is the first turn, request 2 is the correction, request 3 is the second turn.
        Assert.Equal(3, client.Requests.Count);

        string correction = client.Requests[1].Last(message => message.Role == ChatRole.User).Text!;
        Assert.Contains("did not meet these requirements", correction, StringComparison.Ordinal);
        Assert.Contains(RuleNames.MinLength, correction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARejectedAnswerStaysInTheConversationHistory()
    {
        // A known wart, pinned rather than hidden: the model can see its own refused attempt on the
        // next turn, because a session cannot be rewound.
        ScriptedChatClient client = new("No.", Reply("corrected"), Reply("second turn"));
        (AIAgent agent, GuardrailRecorder recorder) = SupportAgentFactory.Create(client, Retriever());

        await new AgentRunner(agent, "test", new RecorderTelemetrySource(recorder))
            .RunAsync([Conversation("First question.", "Second question.")], 1, "tier2", "d.jsonl",
                cancellationToken: Ct);

        IReadOnlyList<ChatMessage> lastTurn = client.Requests[2];

        Assert.Contains(lastTurn, message =>
            message.Role == ChatRole.Assistant && message.Text == "No.");
    }

    [Fact]
    public async Task TheRecordedAttemptCountCoversTheWholeConversation()
    {
        ScriptedChatClient client = new("No.", Reply("corrected"), Reply("second turn"));
        (AIAgent agent, GuardrailRecorder recorder) = SupportAgentFactory.Create(client, Retriever());

        RunArtifact run = await new AgentRunner(agent, "test", new RecorderTelemetrySource(recorder))
            .RunAsync([Conversation("First question.", "Second question.")], 1, "tier2", "d.jsonl",
                cancellationToken: Ct);

        // The recorder reports the last turn, which needed a single attempt.
        Assert.Equal(1, run.Responses[0].Attempts);
    }

    /// <summary>Replays canned turns and records every request it received.</summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly ChatMessage[] _answers;
        private int _index;

        public ScriptedChatClient(params object[] answers) =>
            _answers = answers
                .Select(answer => answer as ChatMessage ?? new ChatMessage(ChatRole.Assistant, (string)answer))
                .ToArray();

        public List<List<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToList());

            return Task.FromResult(new ChatResponse(_answers[Math.Min(_index++, _answers.Length - 1)]));
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




