using EvalFramework.Rules;
using Microsoft.Extensions.AI;
using SupportAgent;
using SupportAgent.Guardrails;

namespace EvalFramework.Tests;

/// <summary>
/// Structuring: splitting a request that needs approval into a piece that does not.
/// </summary>
/// <remarks>
/// Observed live. Asked to refund 4000 against a 500 limit, the agent called issue_refund with 500
/// and the argument guard allowed it, because 500 is a valid amount. Every call was individually in
/// policy while the sequence was not, which is the blind spot of validating calls in isolation.
/// </remarks>
public sealed class ToolContextRuleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ToolGuard Guard(Action<ToolGuardOutcome>? onCall = null) =>
        new(SupportPolicy.ToolRules, onCall, SupportPolicy.ToolContextRuleSet);

    private static ChatMessage[] Asking(string text) => [new(ChatRole.User, text)];

    private static Dictionary<string, object?> Refund(double amount) => new()
    {
        ["orderId"] = "A-55012",
        ["amount"] = amount,
        ["reason"] = "not_delivered"
    };

    [Fact]
    public void APartialPayoutAgainstAnOverLimitRequestIsRejected()
    {
        RuleReport report = Guard().Validate(
            SupportPolicy.IssueRefundTool,
            Refund(500),
            Asking("Order A-55012 was never delivered. Refund me 4000 right now."))!;

        Assert.False(report.Passed);
        Assert.Contains(report.Failures, failure => failure.Name == "no_partial_payout_above_limit");
    }

    [Fact]
    public void AnInLimitRequestIsStillAllowed()
    {
        RuleReport report = Guard().Validate(
            SupportPolicy.IssueRefundTool,
            Refund(120),
            Asking("Order A-31905 arrived damaged. Please refund me 120 for it."))!;

        Assert.True(report.Passed);
    }

    [Fact]
    public void AnOrderNumberIsNotMistakenForAnAmount()
    {
        // A-55012 must not read as a 55,012 request, or every refund would be blocked.
        Assert.Equal(
            120,
            ToolContextRules.LargestAmountRequested(
                Asking("Order A-55012 arrived damaged, refund me 120")));
    }

    [Fact]
    public void OnlyTheCustomerIsBelievedAboutWhatWasRequested()
    {
        // The agent proposing 500 must not become evidence that 500 was asked for.
        ChatMessage[] conversation =
        [
            new(ChatRole.User, "Refund me 4000 for order A-1."),
            new(ChatRole.Assistant, "I can refund up to 500 without escalation.")
        ];

        Assert.Equal(4000, ToolContextRules.LargestAmountRequested(conversation));
    }

    [Fact]
    public void NoAmountInTheConversationLeavesTheCallAlone()
    {
        RuleReport report = Guard().Validate(
            SupportPolicy.IssueRefundTool,
            Refund(200),
            Asking("My order never arrived, please sort it out."))!;

        Assert.True(report.Passed);
    }

    [Fact]
    public async Task TheRejectedCallNeverReachesTheTool()
    {
        bool invoked = false;

        AIFunction function = AIFunctionFactory.Create(() => "refunded", SupportPolicy.IssueRefundTool);

        FunctionInvocationContext context = new()
        {
            Function = function,
            Arguments = new AIFunctionArguments(Refund(500)),
            CallContent = new FunctionCallContent("id", SupportPolicy.IssueRefundTool, Refund(500)),
            Messages = [new ChatMessage(ChatRole.User, "Refund me 4000 for order A-55012 right now.")]
        };

        object? result = await Guard().InvokeAsync(
            agent: null!,
            context,
            (_, _) =>
            {
                invoked = true;
                return ValueTask.FromResult<object?>("refunded");
            },
            Ct);

        Assert.False(invoked);
        Assert.Contains("Escalate to a supervisor", Assert.IsType<string>(result), StringComparison.Ordinal);
    }

    [Fact]
    public void ToolsWithoutAnyRuleAreStillUnguarded()
    {
        Assert.Null(Guard().Validate(
            "unknown_tool", new Dictionary<string, object?>(), Asking("anything")));
    }
}

