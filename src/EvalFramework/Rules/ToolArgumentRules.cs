using System.Globalization;
using System.Text.Json;

namespace EvalFramework.Rules;

public readonly record struct NumericRange(double? Min, double? Max)
{
    public bool Contains(double value) =>
        (Min is not double min || value >= min) && (Max is not double max || value <= max);

    public override string ToString() =>
        $"[{Min?.ToString(CultureInfo.InvariantCulture) ?? "-inf"}, " +
        $"{Max?.ToString(CultureInfo.InvariantCulture) ?? "+inf"}]";
}

/// <summary>
/// Constraints on a single tool's arguments, declared inline next to the tool it guards.
/// </summary>
public sealed class ToolArgumentRule
{
    public required string ToolName { get; init; }

    public IReadOnlyList<string> RequiredArguments { get; init; } = [];

    public IReadOnlyDictionary<string, NumericRange> NumericRanges { get; init; } =
        new Dictionary<string, NumericRange>();

    public IReadOnlyDictionary<string, IReadOnlyList<string>> AllowedValues { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>Tool arguments default to blocking: a bad side effect cannot be undone by a retry.</summary>
    public RuleSeverity Severity { get; init; } = RuleSeverity.Block;
}

/// <summary>
/// Tier 1 layer A. Validates tool arguments before the tool executes, so the agent can be
/// corrected inside its existing ReAct loop rather than after it has already acted.
/// </summary>
public static class ToolArgumentRules
{
    public static RuleReport Evaluate(ToolArgumentRule rule, IReadOnlyDictionary<string, object?> arguments)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(arguments);

        List<CheckResult> checks = [];

        if (rule.RequiredArguments.Count > 0)
        {
            string[] missing = rule.RequiredArguments
                .Where(name => !arguments.TryGetValue(name, out object? value) || IsEmpty(value))
                .ToArray();

            checks.Add(new CheckResult(
                RuleNames.RequiredArguments,
                missing.Length == 0,
                missing.Length == 0 ? "all required arguments present" : $"missing: {string.Join(", ", missing)}",
                rule.Severity));
        }

        foreach ((string name, NumericRange range) in rule.NumericRanges)
        {
            checks.Add(NumericRangeCheck(rule, arguments, name, range));
        }

        foreach ((string name, IReadOnlyList<string> allowed) in rule.AllowedValues)
        {
            checks.Add(AllowedValuesCheck(rule, arguments, name, allowed));
        }

        return new RuleReport(checks);
    }

    private static CheckResult NumericRangeCheck(
        ToolArgumentRule rule,
        IReadOnlyDictionary<string, object?> arguments,
        string name,
        NumericRange range)
    {
        string checkName = $"{RuleNames.NumericRange}:{name}";

        if (!arguments.TryGetValue(name, out object? raw) || raw is null)
        {
            return new CheckResult(checkName, false, $"{name} is missing", rule.Severity);
        }

        if (!TryGetDouble(raw, out double value))
        {
            return new CheckResult(checkName, false, $"{name} is not numeric: '{raw}'", rule.Severity);
        }

        bool passed = range.Contains(value);

        return new CheckResult(
            checkName,
            passed,
            passed
                ? $"{name}={value.ToString(CultureInfo.InvariantCulture)} within {range}"
                : $"{name}={value.ToString(CultureInfo.InvariantCulture)} outside {range}",
            rule.Severity);
    }

    private static CheckResult AllowedValuesCheck(
        ToolArgumentRule rule,
        IReadOnlyDictionary<string, object?> arguments,
        string name,
        IReadOnlyList<string> allowed)
    {
        string checkName = $"{RuleNames.AllowedValues}:{name}";

        if (!arguments.TryGetValue(name, out object? raw) || IsEmpty(raw))
        {
            return new CheckResult(checkName, false, $"{name} is missing", rule.Severity);
        }

        string value = AsString(raw);
        bool passed = allowed.Contains(value, StringComparer.OrdinalIgnoreCase);

        return new CheckResult(
            checkName,
            passed,
            passed ? $"{name}='{value}' allowed" : $"{name}='{value}' not in [{string.Join(", ", allowed)}]",
            rule.Severity);
    }

    private static bool IsEmpty(object? value) =>
        value is null || string.IsNullOrWhiteSpace(AsString(value));

    private static string AsString(object? value) => value switch
    {
        null => string.Empty,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
        JsonElement element => element.ToString(),
        _ => value.ToString() ?? string.Empty
    };

    /// <summary>Tool arguments arrive as JSON, so numbers may be boxed, string, or JsonElement.</summary>
    private static bool TryGetDouble(object value, out double result)
    {
        switch (value)
        {
            case JsonElement { ValueKind: JsonValueKind.Number } element:
                return element.TryGetDouble(out result);
            case JsonElement element:
                return double.TryParse(
                    element.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out result);
            case IConvertible convertible and not string and not bool:
                result = convertible.ToDouble(CultureInfo.InvariantCulture);
                return true;
            default:
                return double.TryParse(
                    value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out result);
        }
    }
}
