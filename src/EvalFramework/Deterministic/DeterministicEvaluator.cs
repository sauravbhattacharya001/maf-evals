using EvalFramework.Datasets;

namespace EvalFramework.Deterministic;

/// <summary>
/// Tier 1. Pure functions over a response string: no network, no model, no clock.
/// Tier 2 reuses this exact scorer so both tiers judge responses identically.
/// </summary>
public static class DeterministicEvaluator
{
    public static DeterministicResult Evaluate(GoldenCase goldenCase, string response)
    {
        ArgumentNullException.ThrowIfNull(goldenCase);
        response ??= string.Empty;

        List<CheckResult> checks = [MinLength(goldenCase, response)];

        if (goldenCase.ExpectedTerms.Count > 0)
        {
            checks.Add(ExpectedTerms(goldenCase, response));
        }

        if (goldenCase.ForbiddenTerms.Count > 0)
        {
            checks.Add(ForbiddenTerms(goldenCase, response));
        }

        if (goldenCase.RequireActionableFormat)
        {
            checks.Add(ActionableFormat(response));
        }

        return new DeterministicResult(goldenCase.Id, checks.All(check => check.Passed), checks);
    }

    private static CheckResult MinLength(GoldenCase goldenCase, string response)
    {
        int length = response.Trim().Length;
        bool passed = length >= goldenCase.MinLength;
        return new CheckResult(
            "min_length",
            passed,
            $"length {length}, required {goldenCase.MinLength}");
    }

    private static CheckResult ExpectedTerms(GoldenCase goldenCase, string response)
    {
        string[] missing = goldenCase.ExpectedTerms
            .Where(term => !response.Contains(term, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return new CheckResult(
            "expected_terms",
            missing.Length == 0,
            missing.Length == 0 ? "all expected terms present" : $"missing: {string.Join(", ", missing)}");
    }

    private static CheckResult ForbiddenTerms(GoldenCase goldenCase, string response)
    {
        string[] present = goldenCase.ForbiddenTerms
            .Where(term => response.Contains(term, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return new CheckResult(
            "forbidden_terms",
            present.Length == 0,
            present.Length == 0 ? "no forbidden terms" : $"present: {string.Join(", ", present)}");
    }

    private static CheckResult ActionableFormat(string response)
    {
        bool passed = response.Contains("1.", StringComparison.Ordinal)
            || response.Contains("- ", StringComparison.Ordinal)
            || response.Contains("* ", StringComparison.Ordinal);

        return new CheckResult(
            "actionable_format",
            passed,
            passed ? "numbered or bulleted steps found" : "no numbered or bulleted steps");
    }
}
