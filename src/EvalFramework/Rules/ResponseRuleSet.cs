namespace EvalFramework.Rules;

/// <summary>
/// A set of response rules. Deliberately a plain object rather than a dataset type, so the
/// same rules can be declared inline next to the agent (Tier 1) or loaded from the golden
/// set (Tier 2) without either side depending on the other.
/// </summary>
public sealed class ResponseRuleSet
{
    public int MinLength { get; init; } = 40;

    public IReadOnlyList<string> ExpectedTerms { get; init; } = [];

    /// <summary>
    /// Groups of alternatives; at least one term from each group must appear.
    /// </summary>
    /// <remarks>
    /// Needed because a policy is usually satisfied by any of several words. Demanding the literal
    /// word "professional" would reject "consult your pharmacist", which is the behaviour actually
    /// wanted. A rule that fails correct output is a broken rule, not a finding.
    /// </remarks>
    public IReadOnlyList<IReadOnlyList<string>> ExpectedAnyTerms { get; init; } = [];

    public IReadOnlyList<string> ForbiddenTerms { get; init; } = [];

    public bool RequireActionableFormat { get; init; } = true;

    /// <summary>Overrides <see cref="DefaultSeverities"/> per rule name.</summary>
    public IReadOnlyDictionary<string, RuleSeverity> Severities { get; init; } =
        new Dictionary<string, RuleSeverity>();

    /// <summary>
    /// Forbidden content blocks because it must never ship. Formatting only warns because a
    /// missing bullet list is a cosmetic defect, not a safety problem.
    /// </summary>
    public static IReadOnlyDictionary<string, RuleSeverity> DefaultSeverities { get; } =
        new Dictionary<string, RuleSeverity>(StringComparer.Ordinal)
        {
            [RuleNames.MinLength] = RuleSeverity.Retry,
            [RuleNames.ExpectedTerms] = RuleSeverity.Retry,
            [RuleNames.ExpectedAnyTerms] = RuleSeverity.Retry,
            [RuleNames.ForbiddenTerms] = RuleSeverity.Block,
            [RuleNames.ActionableFormat] = RuleSeverity.Warn
        };

    public RuleSeverity SeverityFor(string ruleName) =>
        Severities.TryGetValue(ruleName, out RuleSeverity overridden)
            ? overridden
            : DefaultSeverities.TryGetValue(ruleName, out RuleSeverity fallback)
                ? fallback
                : RuleSeverity.Retry;
}

public static class RuleNames
{
    public const string MinLength = "min_length";
    public const string ExpectedTerms = "expected_terms";
    public const string ExpectedAnyTerms = "expected_any_terms";
    public const string ForbiddenTerms = "forbidden_terms";
    public const string ActionableFormat = "actionable_format";
    public const string RequiredArguments = "required_arguments";
    public const string NumericRange = "numeric_range";
    public const string AllowedValues = "allowed_values";
}
