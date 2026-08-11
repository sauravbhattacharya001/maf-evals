using EvalFramework.Retrieval;
using EvalFramework.Execution;
using EvalFramework.Rules;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SupportAgent.Guardrails;
using SupportAgent.Retrieval;

namespace SupportAgent;

/// <summary>Per-request Tier 1 telemetry, consumed by Tier 2 and aggregated by Tier 3.</summary>
public sealed class GuardrailRecorder
{
    private readonly List<ToolCallRecord> _toolCalls = [];

    public RetrievalTrace? LastRetrieval { get; private set; }

    public GuardrailOutcome? LastResponse { get; private set; }

        /// <summary>
    /// Every tool invocation seen this request, in order.
    /// </summary>
    /// <remarks>
    /// A snapshot, not the live list. Handing out the mutable instance meant the next request's
    /// Reset cleared calls already captured into the previous record, so evidence of a tool that
    /// really ran disappeared after the fact.
    /// </remarks>
    public IReadOnlyList<ToolCallRecord> ToolCalls => _toolCalls.ToArray();

    public void Reset()
    {
        LastRetrieval = null;
        LastResponse = null;
        _toolCalls.Clear();
    }

    internal void RecordRetrieval(RetrievalTrace trace) => LastRetrieval = trace;

    internal void RecordResponse(GuardrailOutcome outcome) => LastResponse = outcome;

    public void RecordToolCall(ToolCallRecord record) => _toolCalls.Add(record);
}

public sealed record SupportAgentOptions
{
    public int TopK { get; init; } = 3;

    public int MaxAttempts { get; init; } = 2;

    public ResponseRuleSet Rules { get; init; } = SupportPolicy.BaselineRules;
}

/// <summary>
/// Composes the support agent with Tier 1 guardrails.
/// </summary>
/// <remarks>
/// Pipeline order, outermost first: retrieval, response guard, tool guard. Retrieval must wrap the
/// response guard so context is fixed across retries; the tool guard must be innermost so it sees
/// the individual function calls.
/// </remarks>
public static class SupportAgentFactory
{
    public static (AIAgent Agent, GuardrailRecorder Recorder) Create(
        IChatClient chatClient,
        IRetriever retriever,
        SupportAgentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(retriever);

        options ??= new SupportAgentOptions();
        GuardrailRecorder recorder = new();

        ChatClientAgent inner = new(
            chatClient,
            new ChatClientAgentOptions
            {
                Name = "SupportAgent",
                ChatOptions = new ChatOptions
                {
                    Instructions = SupportPolicy.Instructions,
                    Tools = [.. SupportPolicy.CreateTools()]
                }
            });

        RetrievalAugmenter augmenter = new(retriever, options.TopK, recorder.RecordRetrieval);
        ResponseGuard responseGuard = new(options.Rules, options.MaxAttempts, recorder.RecordResponse);
        ToolGuard toolGuard = new(
            SupportPolicy.ToolRules,
            outcome => recorder.RecordToolCall(
                new ToolCallRecord(outcome.ToolName, outcome.Arguments, outcome.Rejected)));

        AIAgent agent = new AIAgentBuilder(inner)
            .Use(
                runFunc: (messages, session, runOptions, innerAgent, cancellationToken) =>
                    augmenter.RunAsync(
                        messages,
                        session,
                        runOptions,
                        (m, s, o, ct) => innerAgent.RunAsync(m, s, o, ct),
                        cancellationToken),
                runStreamingFunc: null)
            .UseResponseGuard(responseGuard)
            .UseToolGuard(toolGuard)
            .Build();

        return (agent, recorder);
    }
}





