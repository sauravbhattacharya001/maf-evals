namespace EvalFramework.Rules;

/// <summary>
/// Tier 1's response rules: pure functions over a response string. No network, no model,
/// no clock, so they are cheap enough to run on every request in the agent's hot path and
/// deterministic enough to gate a pull request.
/// </summary>
public static class ResponseRules
{
    public static RuleReport Evaluate(ResponseRuleSet rules, string? response)
    {
        ArgumentNullException.ThrowIfNull(rules);
        response ??= string.Empty;

        List<CheckResult> checks = [MinLength(rules, response)];

        if (rules.ExpectedTerms.Count > 0)
        {
            checks.Add(ExpectedTerms(rules, response));
        }

        if (rules.ForbiddenTerms.Count > 0)
        {
            checks.Add(ForbiddenTerms(rules, response));
        }

        if (rules.RequireActionableFormat)
        {
            checks.Add(ActionableFormat(rules, response));
        }

        return new RuleReport(checks);
    }

    private static CheckResult MinLength(ResponseRuleSet rules, string response)
    {
        int length = response.Trim().Length;

        return new CheckResult(
            RuleNames.MinLength,
            length >= rules.MinLength,
            $"length {length}, required {rules.MinLength}",
            rules.SeverityFor(RuleNames.MinLength));
    }

    private static CheckResult ExpectedTerms(ResponseRuleSet rules, string response)
    {
        string[] missing = rules.ExpectedTerms
            .Where(term => !response.Contains(term, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return new CheckResult(
            RuleNames.ExpectedTerms,
            missing.Length == 0,
            missing.Length == 0 ? "all expected terms present" : $"missing: {string.Join(", ", missing)}",
            rules.SeverityFor(RuleNames.ExpectedTerms));
    }

    private static CheckResult ForbiddenTerms(ResponseRuleSet rules, string response)
    {
        string[] present = rules.ForbiddenTerms
            .Where(term => response.Contains(term, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return new CheckResult(
            RuleNames.ForbiddenTerms,
            present.Length == 0,
            present.Length == 0 ? "no forbidden terms" : $"present: {string.Join(", ", present)}",
            rules.SeverityFor(RuleNames.ForbiddenTerms));
    }

    private static CheckResult ActionableFormat(ResponseRuleSet rules, string response)
    {
        bool passed = response.Contains("1.", StringComparison.Ordinal)
            || response.Contains("- ", StringComparison.Ordinal)
            || response.Contains("* ", StringComparison.Ordinal);

        return new CheckResult(
            RuleNames.ActionableFormat,
            passed,
            passed ? "numbered or bulleted steps found" : "no numbered or bulleted steps",
            rules.SeverityFor(RuleNames.ActionableFormat));
    }
}
