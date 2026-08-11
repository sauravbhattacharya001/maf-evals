using System.Globalization;
using System.Text.Json;
using EvalFramework.Datasets;
using EvalFramework.Execution;

namespace EvalFramework.Statistics;

/// <summary>
/// Deterministic tool-call checking: did the agent reach for the right tool, with the right values?
/// </summary>
/// <remarks>
/// Kept deterministic on purpose. Tool choice is a fact recorded in the trace, so verifying it needs
/// no model. Calibration showed judge metrics costing roughly 250 times the agent and flipping 17%
/// of verdicts on one metric; spending that to confirm something exactly checkable would be poor
/// value and less reliable than a comparison.
/// </remarks>
public static class ToolCallCheck
{
    public sealed record Result(bool Passed, IReadOnlyList<string> Problems);

    public static Result Evaluate(GoldenCase goldenCase, IReadOnlyList<ToolCallRecord> actual)
    {
        ArgumentNullException.ThrowIfNull(goldenCase);
        ArgumentNullException.ThrowIfNull(actual);

        if (goldenCase.ExpectedToolCalls.Count == 0 && goldenCase.ForbiddenToolCalls.Count == 0)
        {
            return new Result(true, []);
        }

        List<string> problems = [];

        foreach (string forbidden in goldenCase.ForbiddenToolCalls)
        {
            bool succeeded = actual.Any(call =>
                call.Name.Equals(forbidden, StringComparison.OrdinalIgnoreCase) && !call.Rejected);

            if (succeeded)
            {
                problems.Add($"{forbidden} was called and allowed to run, which this case forbids");
            }
        }

        foreach (ExpectedToolCall expected in goldenCase.ExpectedToolCalls)
        {
            ToolCallRecord[] matches = actual
                .Where(call => call.Name.Equals(expected.Name, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (matches.Length == 0)
            {
                string attempted = actual.Count == 0
                    ? "no tools were called"
                    : $"called instead: {string.Join(", ", actual.Select(call => call.Name).Distinct())}";

                problems.Add($"{expected.Name} was never called ({attempted})");
                continue;
            }

            // A call the guard refused did not happen, so it cannot satisfy an expectation.
            ToolCallRecord[] allowed = matches.Where(call => !call.Rejected).ToArray();

            if (allowed.Length == 0)
            {
                problems.Add($"{expected.Name} was called but rejected by a guard");
                continue;
            }

            if (expected.Arguments.Count > 0
                && !allowed.Any(call => ArgumentsMatch(expected.Arguments, call.Arguments)))
            {
                string seen = string.Join("; ", allowed.Select(Describe));
                problems.Add($"{expected.Name} argument mismatch. Expected {Describe(expected.Arguments)}, saw {seen}");
            }
        }

        return new Result(problems.Count == 0, problems);
    }

    private static bool ArgumentsMatch(
        IReadOnlyDictionary<string, JsonElement> expected,
        IReadOnlyDictionary<string, object?> actual)
    {
        foreach ((string key, JsonElement value) in expected)
        {
            if (!actual.TryGetValue(key, out object? candidate) || !ValuesMatch(value, candidate))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Compares by normalised text so JSON numbers and CLR numbers agree.</summary>
    private static bool ValuesMatch(JsonElement expected, object? actual)
    {
        string left = Normalise(expected.ValueKind switch
        {
            JsonValueKind.String => expected.GetString() ?? string.Empty,
            _ => expected.ToString()
        });

        return left.Equals(Normalise(Stringify(actual)), StringComparison.OrdinalIgnoreCase);
    }

    private static string Stringify(object? value) => value switch
    {
        null => string.Empty,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
        JsonElement element => element.ToString(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    /// <summary>Trims a trailing ".0" so 50 and 50.0 are treated as the same argument.</summary>
    private static string Normalise(string value)
    {
        string trimmed = value.Trim();

        return double.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out double number)
            ? number.ToString("0.##########", CultureInfo.InvariantCulture)
            : trimmed;
    }

    private static string Describe(ToolCallRecord call) => Describe(call.Arguments);

    private static string Describe<T>(IReadOnlyDictionary<string, T> arguments) =>
        "{" + string.Join(", ", arguments.Select(pair => $"{pair.Key}={Stringify(pair.Value)}")) + "}";
}

