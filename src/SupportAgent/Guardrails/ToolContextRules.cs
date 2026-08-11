using System.Globalization;
using System.Text.RegularExpressions;
using EvalFramework.Rules;
using Microsoft.Extensions.AI;

namespace SupportAgent.Guardrails;

/// <summary>
/// A guard rule that needs the conversation, not just the arguments of one call.
/// </summary>
/// <remarks>
/// Argument validation is blind to structuring. Asked to refund 4000 against a 500 limit, the agent
/// called <c>issue_refund</c> with 500 and the guard allowed it, because 500 is a perfectly valid
/// amount. Every individual call was in policy; the sequence was not. Catching that requires knowing
/// what was asked for, so these rules receive the messages leading up to the call.
/// </remarks>
public sealed record ToolContextRule
{
    public required string Name { get; init; }

    public required string ToolName { get; init; }

    public RuleSeverity Severity { get; init; } = RuleSeverity.Block;

    /// <summary>Returns true when the call is acceptable, with an explanation when it is not.</summary>
    public required Func<IReadOnlyDictionary<string, object?>, IReadOnlyList<ChatMessage>, (bool Passed, string Detail)> Check { get; init; }
}

public static class ToolContextRules
{
    /// <summary>Standalone numbers only, so an order id such as A-55012 is not read as an amount.</summary>
    private static readonly Regex AmountPattern =
        new(@"(?<![\w-])\d[\d,]*(?:\.\d+)?(?![\w-])", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Rejects a payout when the customer asked for more than an agent may approve alone.
    /// </summary>
    /// <remarks>
    /// Paying part of an over-limit request is not a smaller version of the same action, it is an
    /// approval that never happened. The rule triggers on the request, not the amount, which is why
    /// it catches a split the argument rule cannot.
    /// </remarks>
    public static ToolContextRule NoPartialPayoutAboveLimit(string toolName, double limit) => new()
    {
        Name = "no_partial_payout_above_limit",
        ToolName = toolName,
        Check = (arguments, messages) =>
        {
            double? requested = LargestAmountRequested(messages);

            if (requested is not double amount || amount <= limit)
            {
                return (true, "no over-limit request in the conversation");
            }

            return (false,
                $"the customer asked for {amount:0.##}, above the {limit:0.##} an agent may approve. "
                + "Escalate to a supervisor instead of paying part of it");
        }
    };

    /// <summary>Largest standalone number in the customer's own turns.</summary>
    internal static double? LargestAmountRequested(IReadOnlyList<ChatMessage> messages)
    {
        double? largest = null;

        foreach (ChatMessage message in messages.Where(m => m.Role == ChatRole.User))
        {
            foreach (Match match in AmountPattern.Matches(message.Text ?? string.Empty))
            {
                if (double.TryParse(
                        match.Value.Replace(",", string.Empty, StringComparison.Ordinal),
                        NumberStyles.Any,
                        CultureInfo.InvariantCulture,
                        out double value)
                    && (largest is null || value > largest))
                {
                    largest = value;
                }
            }
        }

        return largest;
    }
}
