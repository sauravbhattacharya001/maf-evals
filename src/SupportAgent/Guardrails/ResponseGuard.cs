using EvalFramework.Rules;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace SupportAgent.Guardrails;

/// <summary>Runs the next stage of the agent pipeline.</summary>
public delegate Task<AgentResponse> AgentContinuation(
    IEnumerable<ChatMessage> messages,
    AgentSession? session,
    AgentRunOptions? options,
    CancellationToken cancellationToken);

/// <summary>
/// Tier 1 layer B. Validates the final response and retries with corrective feedback.
/// </summary>
/// <remarks>
/// <para>
/// Written against a continuation delegate rather than <see cref="AIAgent"/> so the retry logic
/// is testable without a model, and wired into a real pipeline by
/// <see cref="GuardrailAgentBuilderExtensions"/>.
/// </para>
/// <para>
/// Retries only happen for <see cref="RuleSeverity.Retry"/> and above. A warn-level failure is
/// not worth another model call.
/// </para>
/// </remarks>
public sealed class ResponseGuard(
    Func<IEnumerable<ChatMessage>, ResponseRuleSet> ruleSelector,
    int maxAttempts = 2,
    Action<GuardrailOutcome>? onOutcome = null)
{
    private readonly Func<IEnumerable<ChatMessage>, ResponseRuleSet> _ruleSelector =
        ruleSelector ?? throw new ArgumentNullException(nameof(ruleSelector));

    private readonly int _maxAttempts = maxAttempts >= 1
        ? maxAttempts
        : throw new ArgumentOutOfRangeException(nameof(maxAttempts), "maxAttempts must be at least 1.");

    public ResponseGuard(ResponseRuleSet rules, int maxAttempts = 2, Action<GuardrailOutcome>? onOutcome = null)
        : this(_ => rules, maxAttempts, onOutcome)
    {
    }

    public async Task<AgentResponse> RunAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AgentContinuation next,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(next);

        List<ChatMessage> original = messages.ToList();
        ResponseRuleSet rules = _ruleSelector(original);

        IEnumerable<ChatMessage> attemptMessages = original;
        AgentResponse response;
        RuleReport report;
        int attempt = 0;

        while (true)
        {
            attempt++;
            response = await next(attemptMessages, session, options, cancellationToken).ConfigureAwait(false);
            report = ResponseRules.Evaluate(rules, response.Text);

            if (report.Passed || !report.ShouldRetry || attempt >= _maxAttempts)
            {
                break;
            }

            attemptMessages = BuildRetryMessages(original, session, response.Text ?? string.Empty, report);
        }

        GuardrailOutcome outcome = new(attempt, report, Degraded: !report.Passed);
        onOutcome?.Invoke(outcome);

        if (report.ShouldBlock)
        {
            throw new GuardrailBlockedException(outcome);
        }

        return response;
    }

    /// <summary>
    /// With a session the agent already remembers the exchange, so only the correction is sent.
    /// Without one the whole context must be replayed, including the rejected answer.
    /// </summary>
    private static List<ChatMessage> BuildRetryMessages(
        List<ChatMessage> original,
        AgentSession? session,
        string rejected,
        RuleReport report)
    {
        ChatMessage correction = new(ChatRole.User, report.ToCorrectionMessage());

        if (session is not null)
        {
            return [correction];
        }

        return [.. original, new ChatMessage(ChatRole.Assistant, rejected), correction];
    }
}
