using EvalFramework.Rules;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SupportAgent.Guardrails;

namespace EvalFramework.Tests;

public sealed class ResponseGuardTests
{
    private static readonly ResponseRuleSet Rules = new()
    {
        MinLength = 30,
        RequireActionableFormat = false,
        ExpectedTerms = ["order number"],
        ForbiddenTerms = ["double your dose"]
    };

    private const string Good = "Please send support your order number and we will refund it.";
    private const string Short = "No.";
    private const string Unsafe = "You should double your dose and also send your order number now.";

    /// <summary>Returns each scripted answer in turn, recording what it was asked.</summary>
    private static AgentContinuation Script(List<List<ChatMessage>> seen, params string[] answers)
    {
        int index = 0;

        return (messages, _, _, _) =>
        {
            seen.Add(messages.ToList());
            string answer = answers[Math.Min(index++, answers.Length - 1)];
            return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, answer)));
        };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static List<ChatMessage> Ask() => [new(ChatRole.User, "How do I get a refund?")];

    [Fact]
    public async Task CompliantFirstAnswerCostsOnlyOneCall()
    {
        List<List<ChatMessage>> seen = [];
        GuardrailOutcome? outcome = null;

        ResponseGuard guard = new(Rules, maxAttempts: 3, onOutcome: result => outcome = result);
        AgentResponse response = await guard.RunAsync(Ask(), null, null, Script(seen, Good), Ct);

        Assert.Equal(Good, response.Text);
        Assert.Single(seen);
        Assert.Equal(1, outcome!.Attempts);
        Assert.False(outcome.Recovered);
    }

    [Fact]
    public async Task BadAnswerIsRetriedAndTheCorrectionNamesTheBrokenRule()
    {
        List<List<ChatMessage>> seen = [];
        GuardrailOutcome? outcome = null;

        ResponseGuard guard = new(Rules, maxAttempts: 3, onOutcome: result => outcome = result);
        AgentResponse response = await guard.RunAsync(Ask(), null, null, Script(seen, Short, Good), Ct);

        Assert.Equal(Good, response.Text);
        Assert.Equal(2, seen.Count);
        Assert.True(outcome!.Recovered);

        string correction = seen[1][^1].Text;
        Assert.Contains(RuleNames.MinLength, correction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatelessRetryReplaysContextIncludingTheRejectedAnswer()
    {
        List<List<ChatMessage>> seen = [];

        await new ResponseGuard(Rules, maxAttempts: 2).RunAsync(Ask(), null, null, Script(seen, Short, Good), Ct);

        List<ChatMessage> retry = seen[1];
        Assert.Equal(3, retry.Count);
        Assert.Equal(ChatRole.Assistant, retry[1].Role);
        Assert.Equal(Short, retry[1].Text);
    }

    [Fact]
    public async Task SessionRetrySendsOnlyTheCorrectionToAvoidDuplicatingHistory()
    {
        List<List<ChatMessage>> seen = [];
        AgentSession session = await new ChatClientAgent(
            new StubChatClient(), instructions: null, name: "stub").CreateSessionAsync(Ct);

        await new ResponseGuard(Rules, maxAttempts: 2).RunAsync(Ask(), session, null, Script(seen, Short, Good), Ct);

        Assert.Single(seen[1]);
        Assert.Equal(ChatRole.User, seen[1][0].Role);
    }

    [Fact]
    public async Task RetriesAreBoundedByMaxAttempts()
    {
        List<List<ChatMessage>> seen = [];
        GuardrailOutcome? outcome = null;

        ResponseGuard guard = new(Rules, maxAttempts: 3, onOutcome: result => outcome = result);
        await guard.RunAsync(Ask(), null, null, Script(seen, Short), Ct);

        Assert.Equal(3, seen.Count);
        Assert.True(outcome!.Degraded);
        Assert.False(outcome.Recovered);
    }

    [Fact]
    public async Task WarnOnlyFailureDoesNotSpendARetry()
    {
        List<List<ChatMessage>> seen = [];
        ResponseRuleSet formatOnly = new() { MinLength = 1, RequireActionableFormat = true };

        await new ResponseGuard(formatOnly, maxAttempts: 3)
            .RunAsync(Ask(), null, null, Script(seen, "Contact support about it."), Ct);

        Assert.Single(seen);
    }

    [Fact]
    public async Task BlockedContentThrowsRatherThanReachingTheCaller()
    {
        List<List<ChatMessage>> seen = [];
        ResponseGuard guard = new(Rules, maxAttempts: 2);

        GuardrailBlockedException error = await Assert.ThrowsAsync<GuardrailBlockedException>(
            () => guard.RunAsync(Ask(), null, null, Script(seen, Unsafe), Ct));

        Assert.Equal(2, seen.Count);
        Assert.Contains(RuleNames.ForbiddenTerms, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MaxAttemptsMustBeAtLeastOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResponseGuard(Rules, maxAttempts: 0));
    }

    private sealed class StubChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "stub")));

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

