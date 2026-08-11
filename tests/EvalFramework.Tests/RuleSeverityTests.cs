using EvalFramework.Rules;

namespace EvalFramework.Tests;

/// <summary>
/// Severity is what makes the rule engine usable in the hot path: it decides whether a
/// failure is worth another model call, and whether the response may ship at all.
/// </summary>
public sealed class RuleSeverityTests
{
    [Fact]
    public void FormattingAloneWarnsAndDoesNotJustifyARetry()
    {
        ResponseRuleSet rules = new() { MinLength = 1, RequireActionableFormat = true };

        RuleReport report = ResponseRules.Evaluate(rules, "Contact support and we will sort this out.");

        Assert.False(report.Passed);
        Assert.Equal(RuleSeverity.Warn, report.HighestSeverity);
        Assert.False(report.ShouldRetry);
        Assert.False(report.ShouldBlock);
    }

    [Fact]
    public void MissingExpectedTermTriggersRetryButNotBlock()
    {
        ResponseRuleSet rules = new()
        {
            MinLength = 1,
            RequireActionableFormat = false,
            ExpectedTerms = ["order number"]
        };

        RuleReport report = ResponseRules.Evaluate(rules, "We will look into it.");

        Assert.True(report.ShouldRetry);
        Assert.False(report.ShouldBlock);
    }

    [Fact]
    public void ForbiddenContentBlocksBecauseItMustNeverShip()
    {
        ResponseRuleSet rules = new()
        {
            MinLength = 1,
            RequireActionableFormat = false,
            ForbiddenTerms = ["double your dose"]
        };

        RuleReport report = ResponseRules.Evaluate(rules, "You should double your dose.");

        Assert.Equal(RuleSeverity.Block, report.HighestSeverity);
        Assert.True(report.ShouldBlock);
    }

    [Fact]
    public void SeverityCanBeOverriddenPerCase()
    {
        ResponseRuleSet rules = new()
        {
            MinLength = 1,
            RequireActionableFormat = true,
            Severities = new Dictionary<string, RuleSeverity>
            {
                [RuleNames.ActionableFormat] = RuleSeverity.Block
            }
        };

        RuleReport report = ResponseRules.Evaluate(rules, "Contact support.");

        Assert.True(report.ShouldBlock);
    }

    [Fact]
    public void CorrectionMessageNamesOnlyTheRulesWorthRetrying()
    {
        ResponseRuleSet rules = new()
        {
            MinLength = 200,
            RequireActionableFormat = true,
            ExpectedTerms = ["order number"]
        };

        string message = ResponseRules.Evaluate(rules, "No.").ToCorrectionMessage();

        Assert.Contains(RuleNames.MinLength, message, StringComparison.Ordinal);
        Assert.Contains(RuleNames.ExpectedTerms, message, StringComparison.Ordinal);
        Assert.DoesNotContain(RuleNames.ActionableFormat, message, StringComparison.Ordinal);
    }
}
