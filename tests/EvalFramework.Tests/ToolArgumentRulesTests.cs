using System.Text.Json;
using EvalFramework.Rules;

namespace EvalFramework.Tests;

public sealed class ToolArgumentRulesTests
{
    private static readonly ToolArgumentRule Refund = new()
    {
        ToolName = "issue_refund",
        RequiredArguments = ["orderId", "amount"],
        NumericRanges = new Dictionary<string, NumericRange>
        {
            ["amount"] = new(0, 500)
        },
        AllowedValues = new Dictionary<string, IReadOnlyList<string>>
        {
            ["reason"] = ["damaged", "duplicate", "not_delivered"]
        }
    };

    private static RuleReport Evaluate(params (string Key, object? Value)[] arguments) =>
        ToolArgumentRules.Evaluate(Refund, arguments.ToDictionary(pair => pair.Key, pair => pair.Value));

    [Fact]
    public void ValidArgumentsPass()
    {
        RuleReport report = Evaluate(("orderId", "A-1"), ("amount", 120.5), ("reason", "damaged"));

        Assert.True(report.Passed);
    }

    [Fact]
    public void OutOfRangeAmountIsBlockedBeforeTheToolRuns()
    {
        RuleReport report = Evaluate(("orderId", "A-1"), ("amount", 5000), ("reason", "damaged"));

        Assert.True(report.ShouldBlock);
        Assert.Contains(report.Failures, failure => failure.Name == $"{RuleNames.NumericRange}:amount");
    }

    [Fact]
    public void MissingRequiredArgumentIsCaught()
    {
        RuleReport report = Evaluate(("amount", 10), ("reason", "damaged"));

        Assert.Contains(report.Failures, failure => failure.Name == RuleNames.RequiredArguments);
        Assert.Contains("orderId", report.Failures.First(f => f.Name == RuleNames.RequiredArguments).Detail);
    }

    [Fact]
    public void BlankStringCountsAsMissing()
    {
        RuleReport report = Evaluate(("orderId", "   "), ("amount", 10), ("reason", "damaged"));

        Assert.Contains(report.Failures, failure => failure.Name == RuleNames.RequiredArguments);
    }

    [Fact]
    public void ValueOutsideTheAllowedSetIsRejected()
    {
        RuleReport report = Evaluate(("orderId", "A-1"), ("amount", 10), ("reason", "because_i_said_so"));

        Assert.Contains(report.Failures, failure => failure.Name == $"{RuleNames.AllowedValues}:reason");
    }

    [Fact]
    public void JsonElementArgumentsAreUnderstood()
    {
        // Tool arguments arrive as JSON, so the guard must not depend on boxed CLR numbers.
        using JsonDocument document = JsonDocument.Parse(
            """{"orderId":"A-1","amount":250,"reason":"duplicate"}""");

        Dictionary<string, object?> arguments = document.RootElement
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value);

        Assert.True(ToolArgumentRules.Evaluate(Refund, arguments).Passed);
    }

    [Fact]
    public void NonNumericAmountFailsInsteadOfThrowing()
    {
        RuleReport report = Evaluate(("orderId", "A-1"), ("amount", "lots"), ("reason", "damaged"));

        Assert.Contains(report.Failures, failure => failure.Name == $"{RuleNames.NumericRange}:amount");
    }
}
