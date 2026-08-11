using EvalFramework.Rules;

namespace SupportAgent.Guardrails;

/// <summary>What Tier 1 did on one request. Emitted as telemetry and aggregated later by Tier 3.</summary>
public sealed record GuardrailOutcome(
    int Attempts,
    RuleReport FinalReport,
    bool Degraded)
{
    /// <summary>True when a retry was spent and the result eventually satisfied the rules.</summary>
    public bool Recovered => Attempts > 1 && FinalReport.Passed;
}

/// <summary>
/// Thrown when a <see cref="RuleSeverity.Block"/> rule still fails after every attempt.
/// The caller decides how to surface this; it must never be silently returned to a user.
/// </summary>
public sealed class GuardrailBlockedException(GuardrailOutcome outcome)
    : Exception(BuildMessage(outcome))
{
    public GuardrailOutcome Outcome { get; } = outcome;

    private static string BuildMessage(GuardrailOutcome outcome)
    {
        IEnumerable<string> blocked = outcome.FinalReport.Failures
            .Where(failure => failure.Severity == RuleSeverity.Block)
            .Select(failure => $"{failure.Name} ({failure.Detail})");

        return $"Response blocked after {outcome.Attempts} attempt(s): {string.Join("; ", blocked)}";
    }
}
