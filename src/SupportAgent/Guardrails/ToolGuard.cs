using EvalFramework.Rules;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace SupportAgent.Guardrails;

/// <summary>Records a rejected tool call so Tier 3 can count how often the agent tried it.</summary>
public sealed record ToolGuardOutcome(string ToolName, RuleReport Report);

/// <summary>
/// Tier 1 layer A. Validates tool arguments before the tool runs.
/// </summary>
/// <remarks>
/// On violation the guard does not invoke the tool. It returns an explanatory string as the tool
/// result, which the model reads on its next loop iteration and corrects from. This is the cheapest
/// correction available: it reuses the ReAct iteration the agent was already going to take, and it
/// prevents side effects that a response-level retry could not undo.
/// </remarks>
public sealed class ToolGuard(IEnumerable<ToolArgumentRule> rules, Action<ToolGuardOutcome>? onRejected = null)
{
    private readonly Dictionary<string, ToolArgumentRule> _rules =
        rules.ToDictionary(rule => rule.ToolName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns <see langword="null"/> when the tool has no declared constraints.</summary>
    public RuleReport? Validate(string toolName, IReadOnlyDictionary<string, object?> arguments)
    {
        return _rules.TryGetValue(toolName, out ToolArgumentRule? rule)
            ? ToolArgumentRules.Evaluate(rule, arguments)
            : null;
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
        RuleReport? report = Validate(toolName, context.Arguments);

        if (report is not null && !report.Passed && report.HighestSeverity >= RuleSeverity.Retry)
        {
            onRejected?.Invoke(new ToolGuardOutcome(toolName, report));
            return Explain(toolName, report);
        }

        return await next(context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Phrased as an instruction because the model reads this as the tool's output.</summary>
    internal static string Explain(string toolName, RuleReport report)
    {
        IEnumerable<string> problems = report.Failures.Select(failure => $"- {failure.Name}: {failure.Detail}");

        return $"The call to '{toolName}' was rejected before it ran:\n"
            + string.Join("\n", problems)
            + "\nCorrect the arguments and try again, or explain the limitation to the customer.";
    }
}
