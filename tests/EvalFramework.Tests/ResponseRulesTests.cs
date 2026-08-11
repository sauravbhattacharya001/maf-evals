using EvalFramework.Rules;

namespace EvalFramework.Tests;

public sealed class ResponseRulesTests
{
    private static ResponseRuleSet Rules(bool requireFormat = true) => new()
    {
        ExpectedTerms = ["order number"],
        ForbiddenTerms = ["I can't help"],
        MinLength = 40,
        RequireActionableFormat = requireFormat
    };

    [Fact]
    public void CompliantResponsePassesEveryRule()
    {
        RuleReport report = ResponseRules.Evaluate(
            Rules(),
            "Here is how to get a refund:\n1. Open your account.\n2. Send support your order number.");

        Assert.True(report.Passed);
        Assert.Empty(report.Failures);
        Assert.Null(report.HighestSeverity);
    }

    [Fact]
    public void ReportsEveryBrokenRuleRatherThanStoppingAtTheFirst()
    {
        RuleReport report = ResponseRules.Evaluate(Rules(), "I can't help.");

        Assert.False(report.Passed);
        Assert.Equal(
            [RuleNames.MinLength, RuleNames.ExpectedTerms, RuleNames.ForbiddenTerms, RuleNames.ActionableFormat],
            report.Failures.Select(failure => failure.Name).ToArray());
    }

    [Fact]
    public void FormatRuleIsSkippedWhenTheCaseDoesNotRequireIt()
    {
        RuleReport report = ResponseRules.Evaluate(
            Rules(requireFormat: false),
            "Please contact support with your order number and we will refund the duplicate charge.");

        Assert.True(report.Passed);
        Assert.DoesNotContain(report.Checks, check => check.Name == RuleNames.ActionableFormat);
    }

    [Fact]
    public void MatchingIsCaseInsensitiveSoWordingChangesDoNotCauseFalseFailures()
    {
        RuleReport report = ResponseRules.Evaluate(
            Rules(requireFormat: false),
            "Send us your ORDER NUMBER and we will process the refund immediately today.");

        Assert.True(report.Passed);
    }

    [Fact]
    public void NullResponseIsTreatedAsEmptyRatherThanThrowing()
    {
        RuleReport report = ResponseRules.Evaluate(Rules(), null);

        Assert.False(report.Passed);
    }
}
