using EvalFramework.Rules;
using Microsoft.Extensions.AI;
using SupportAgent.Guardrails;

namespace EvalFramework.Tests;

public sealed class ToolGuardTests
{
    private static readonly ToolArgumentRule RefundRule = new()
    {
        ToolName = "issue_refund",
        RequiredArguments = ["orderId"],
        NumericRanges = new Dictionary<string, NumericRange> { ["amount"] = new(0, 500) }
    };

    private static ToolGuard Guard(Action<ToolGuardOutcome>? onRejected = null) =>
        new([RefundRule], onRejected);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FunctionInvocationContext Context(string toolName, Dictionary<string, object?> arguments)
    {
        AIFunction function = AIFunctionFactory.Create(() => "ok", toolName);

        return new FunctionInvocationContext
        {
            Function = function,
            Arguments = new AIFunctionArguments(arguments),
            CallContent = new FunctionCallContent("call-1", toolName, arguments)
        };
    }

    [Fact]
    public void ToolsWithoutDeclaredConstraintsAreNotValidated()
    {
        Assert.Null(Guard().Validate("lookup_order", new Dictionary<string, object?>()));
    }

    [Fact]
    public async Task ValidArgumentsReachTheTool()
    {
        bool invoked = false;

        object? result = await Guard().InvokeAsync(
            agent: null!,
            Context("issue_refund", new() { ["orderId"] = "A-1", ["amount"] = 100 }),
            (_, _) =>
            {
                invoked = true;
                return ValueTask.FromResult<object?>("refunded");
            },
            Ct);

        Assert.True(invoked);
        Assert.Equal("refunded", result);
    }

    [Fact]
    public async Task OverLimitRefundNeverReachesTheTool()
    {
        bool invoked = false;
        ToolGuardOutcome? rejected = null;

        object? result = await Guard(outcome => rejected = outcome).InvokeAsync(
            agent: null!,
            Context("issue_refund", new() { ["orderId"] = "A-1", ["amount"] = 5000 }),
            (_, _) =>
            {
                invoked = true;
                return ValueTask.FromResult<object?>("refunded");
            },
            Ct);

        Assert.False(invoked);
        Assert.Equal("issue_refund", rejected!.ToolName);

        string explanation = Assert.IsType<string>(result);
        Assert.Contains("rejected before it ran", explanation, StringComparison.Ordinal);
        Assert.Contains("amount", explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingRequiredArgumentIsRejected()
    {
        object? result = await Guard().InvokeAsync(
            agent: null!,
            Context("issue_refund", new() { ["amount"] = 10 }),
            (_, _) => ValueTask.FromResult<object?>("refunded"),
            Ct);

        Assert.Contains("orderId", Assert.IsType<string>(result), StringComparison.Ordinal);
    }

    [Fact]
    public void ExplanationTellsTheModelWhatToDoNext()
    {
        RuleReport report = ToolArgumentRules.Evaluate(
            RefundRule,
            new Dictionary<string, object?> { ["orderId"] = "A-1", ["amount"] = 900 });

        string explanation = ToolGuard.Explain("issue_refund", report);

        Assert.Contains("Correct the arguments", explanation, StringComparison.Ordinal);
    }
}
