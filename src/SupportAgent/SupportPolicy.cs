using System.ComponentModel;
using EvalFramework.Rules;
using Microsoft.Extensions.AI;

namespace SupportAgent;

/// <summary>
/// The agent's behavioural contract, declared inline next to the agent it governs.
/// </summary>
/// <remarks>
/// These are the same rule types the golden set uses. Tier 1 enforces them per request and Tier 2
/// gates on them in CI, so a rule can never drift between production and the test suite.
/// </remarks>
public static class SupportPolicy
{
    public const string Instructions = """
        You are a customer support agent.
        Answer concisely and politely, using numbered steps.
        State exactly what information support will need, such as the order number.
        Never give medical, legal, or financial advice; refer the customer to a qualified professional.
        """;

    /// <summary>Applies to every response, regardless of topic.</summary>
    public static ResponseRuleSet BaselineRules { get; } = new()
    {
        MinLength = 40,
        RequireActionableFormat = true,
        ForbiddenTerms = ["double your dose", "take twice", "guaranteed refund"],
        Severities = new Dictionary<string, RuleSeverity>
        {
            [RuleNames.ActionableFormat] = RuleSeverity.Warn
        }
    };

    /// <summary>Refund limits mirror the policy in <c>corpus/refunds.md</c>.</summary>
    public static IReadOnlyList<ToolArgumentRule> ToolRules { get; } =
    [
        new ToolArgumentRule
        {
            ToolName = "issue_refund",
            RequiredArguments = ["orderId", "amount", "reason"],
            NumericRanges = new Dictionary<string, NumericRange> { ["amount"] = new(0, 500) },
            AllowedValues = new Dictionary<string, IReadOnlyList<string>>
            {
                ["reason"] = ["damaged", "duplicate", "not_delivered"]
            }
        },
        new ToolArgumentRule
        {
            ToolName = "lookup_order",
            RequiredArguments = ["orderId"]
        }
    ];

    /// <summary>Stand-in tools so the tool guard has something real to protect.</summary>
    public static IReadOnlyList<AITool> CreateTools() =>
    [
        AIFunctionFactory.Create(LookupOrder),
        AIFunctionFactory.Create(IssueRefund)
    ];

    [Description("Looks up the current status of an order.")]
    private static string LookupOrder(
        [Description("The customer's order number.")] string orderId) =>
        $"Order {orderId}: dispatched, tracking has not updated for 9 days.";

    [Description("Issues a refund for an order.")]
    private static string IssueRefund(
        [Description("The customer's order number.")] string orderId,
        [Description("Refund amount.")] double amount,
        [Description("One of: damaged, duplicate, not_delivered.")] string reason) =>
        $"Refund of {amount} issued for order {orderId} ({reason}).";
}
