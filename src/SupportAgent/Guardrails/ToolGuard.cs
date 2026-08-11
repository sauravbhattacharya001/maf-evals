using EvalFramework.Rules;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace SupportAgent.Guardrails;

/// <summary>Records a tool invocation the guard saw, and whether it was allowed to run.</summary>
public sealed record ToolGuardOutcome(
    string ToolName,
    IReadOnlyDictionary<string, object?> Arguments,
    RuleReport? Report,
    bool Rejected);

/// <summary>
/// Tier 1 layer A. Validates tool arguments before the tool runs.
/// </summary>
/// <remarks>
/// On violation the guard does not invoke the tool. It returns an explanatory string as the tool
/// result, which the model reads on its next loop iteration and corrects from. This is the cheapest
/// correction available: it reuses the ReAct iteration the agent was already going to take, and it
/// prevents side effects that a response-level retry could not undo.
/// </remarks>
public sealed class ToolGuard(
    IEnumerable<ToolArgumentRule> rules,
    Action<ToolGuardOutcome>? onCall = null,
    IEnumerable<ToolContextRule>? contextRules = null)
{
    private readonly Dictionary<string, ToolArgumentRule> _rules =
        rules.ToDictionary(rule => rule.ToolName, StringComparer.OrdinalIgnoreCase);

    private readonly ToolContextRule[] _contextRules = contextRules?.ToArray() ?? [];

    /// <summary>Returns <see langword="null"/> when the tool has no declared constraints.</summary>
    public RuleReport? Validate(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        IReadOnlyList<ChatMessage>? messages = null)
    {
        List<CheckResult> checks = [];

        if (_rules.TryGetValue(toolName, out ToolArgumentRule? rule))
        {
            checks.AddRange(ToolArgumentRules.Evaluate(rule, arguments).Checks);
        }

        // Conversation-aware rules run even for tools with no argument constraints, because the
        // failure they catch lives in the request rather than in the values passed.
        foreach (ToolContextRule contextRule in _contextRules
            .Where(r => r.ToolName.Equals(toolName, StringComparison.OrdinalIgnoreCase)))
        {
            (bool passed, string detail) = contextRule.Check(arguments, messages ?? []);
            checks.Add(new CheckResult(contextRule.Name, passed, detail, contextRule.Severity));
        }

        return checks.Count == 0 ? null : new RuleReport(checks);
    }

    /// <summary>Middleware entry point for <c>AIAgentBuilder.Use</c>.</summary>
    public async ValueTask<object?> InvokeAsync(
        AIAgent agent,
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        string toolName = context.Function.Name;
        RuleReport? report = Validate(toolName, context.Arguments, context.Messages.ToArray());
        bool rejected = report is not null && !report.Passed && report.HighestSeverity >= RuleSeverity.Retry;

        // Every invocation is recorded, not only refusals: an agent that never reached for a tool
        // and one that reached and was stopped are different behaviours.
        onCall?.Invoke(new ToolGuardOutcome(toolName, Snapshot(context.Arguments), report, rejected));

        if (rejected)
        {
            return Explain(toolName, report!);
        }

        return await next(context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Copied because the invocation context is reused after the call completes.</summary>
    private static IReadOnlyDictionary<string, object?> Snapshot(IReadOnlyDictionary<string, object?> arguments) =>
        new Dictionary<string, object?>(arguments, StringComparer.OrdinalIgnoreCase);

    /// <summary>Phrased as an instruction because the model reads this as the tool's output.</summary>
    internal static string Explain(string toolName, RuleReport report)
    {
        IEnumerable<string> problems = report.Failures.Select(failure => $"- {failure.Name}: {failure.Detail}");

        return $"The call to '{toolName}' was rejected before it ran:\n"
            + string.Join("\n", problems)
            + "\nCorrect the arguments and try again, or explain the limitation to the customer.";
    }
}



