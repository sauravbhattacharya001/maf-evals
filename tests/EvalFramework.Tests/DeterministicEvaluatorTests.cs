using EvalFramework.Datasets;
using EvalFramework.Deterministic;

namespace EvalFramework.Tests;

public sealed class DeterministicEvaluatorTests
{
    private static GoldenCase Case(bool requireFormat = true) => new()
    {
        Id = "sample",
        Query = "How do I get a refund?",
        ExpectedTerms = ["order number"],
        ForbiddenTerms = ["I can't help"],
        MinLength = 40,
        RequireActionableFormat = requireFormat
    };

    [Fact]
    public void CompliantResponsePassesEveryRule()
    {
        DeterministicResult result = DeterministicEvaluator.Evaluate(
            Case(),
            "Here is how to get a refund:\n1. Open your account.\n2. Send support your order number.");

        Assert.True(result.Passed);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void ReportsEveryBrokenRuleRatherThanStoppingAtTheFirst()
    {
        DeterministicResult result = DeterministicEvaluator.Evaluate(Case(), "I can't help.");

        Assert.False(result.Passed);
        Assert.Equal(
            ["min_length", "expected_terms", "forbidden_terms", "actionable_format"],
            result.Failures.Select(failure => failure.Name).ToArray());
    }

    [Fact]
    public void FormatRuleIsSkippedWhenTheCaseDoesNotRequireIt()
    {
        DeterministicResult result = DeterministicEvaluator.Evaluate(
            Case(requireFormat: false),
            "Please contact support with your order number and we will refund the duplicate charge.");

        Assert.True(result.Passed);
        Assert.DoesNotContain(result.Checks, check => check.Name == "actionable_format");
    }

    [Fact]
    public void MatchingIsCaseInsensitiveSoWordingChangesDoNotCauseFalseFailures()
    {
        DeterministicResult result = DeterministicEvaluator.Evaluate(
            Case(requireFormat: false),
            "Send us your ORDER NUMBER and we will process the refund immediately today.");

        Assert.True(result.Passed);
    }

    [Fact]
    public void NullResponseIsTreatedAsEmptyRatherThanThrowing()
    {
        DeterministicResult result = DeterministicEvaluator.Evaluate(Case(), null!);

        Assert.False(result.Passed);
    }
}
