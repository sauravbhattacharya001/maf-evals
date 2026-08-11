using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SupportAgent;
using SupportAgent.Retrieval;

namespace EvalFramework.Tests;

/// <summary>
/// Exercises the composed Tier 1 pipeline end to end against a scripted chat client, so retrieval
/// injection, retries, and telemetry are verified without any model credentials.
/// </summary>
public sealed class SupportAgentPipelineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static KeywordRetriever Retriever()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "corpus")))
        {
            directory = directory.Parent;
        }

        return KeywordRetriever.FromDirectory(Path.Combine(directory!.FullName, "corpus"));
    }

    [Fact]
    public async Task RetrievedPolicyIsInjectedAndRecorded()
    {
        ScriptedChatClient client = new("1. Send support your order number.\n2. We will open a case.");
        (AIAgent agent, GuardrailRecorder recorder) = SupportAgentFactory.Create(client, Retriever());

        await agent.RunAsync("I was charged twice for one order", cancellationToken: Ct);

        Assert.NotNull(recorder.LastRetrieval);
        Assert.NotEmpty(recorder.LastRetrieval!.Chunks);

        ChatMessage system = client.Requests[0].First(message => message.Role == ChatRole.System);
        Assert.Contains("Duplicate charges", system.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ShortAnswerIsRetriedAndTheRecoveryIsRecorded()
    {
        ScriptedChatClient client = new("No.", "1. Send support your order number so we can investigate.");
        (AIAgent agent, GuardrailRecorder recorder) = SupportAgentFactory.Create(client, Retriever());

        AgentResponse response = await agent.RunAsync("My parcel has not arrived", cancellationToken: Ct);

        Assert.Equal(2, client.Requests.Count);
        Assert.True(recorder.LastResponse!.Recovered);
        Assert.Contains("order number", response.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ContextIsRetrievedOnceEvenWhenTheAnswerIsRetried()
    {
        ScriptedChatClient client = new("No.", "1. Send support your order number so we can investigate.");
        (AIAgent agent, GuardrailRecorder recorder) = SupportAgentFactory.Create(client, Retriever());

        await agent.RunAsync("My parcel has not arrived", cancellationToken: Ct);

        // Retrieval sits outside the retry loop, so the trace still describes the final answer.
        Assert.Equal("My parcel has not arrived", recorder.LastRetrieval!.Query);
    }

    [Fact]
    public async Task UnsafeAdviceIsBlockedInsteadOfReturned()
    {
        ScriptedChatClient client = new("You should double your dose while you wait for the replacement.");
        (AIAgent agent, _) = SupportAgentFactory.Create(client, Retriever());

        await Assert.ThrowsAsync<SupportAgent.Guardrails.GuardrailBlockedException>(
            () => agent.RunAsync("A box of my medication was missing", cancellationToken: Ct));
    }

    /// <summary>Replays canned assistant turns and records every request it received.</summary>
    private sealed class ScriptedChatClient(params string[] answers) : IChatClient
    {
        private int _index;

        public List<List<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToList());
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
}
