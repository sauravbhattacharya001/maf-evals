using EvalRunner;

namespace EvalFramework.Tests;

/// <summary>
/// Configuration resolution, which had no tests until it had broken CI three times.
/// </summary>
/// <remarks>
/// GitHub Actions injects an undefined secret or variable as an empty string rather than omitting
/// it, so the usual <c>??</c> idiom silently keeps the empty value. Every case below is a real
/// shape that arrives from a workflow file.
/// </remarks>
public sealed class EnvironmentSettingsTests
{
    private static Func<string, string?> Env(params (string Name, string? Value)[] pairs)
    {
        Dictionary<string, string?> values = pairs.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);

        return name => values.TryGetValue(name, out string? value) ? value : null;
    }

    [Fact]
    public void ASetValueIsUsed()
    {
        Assert.Equal("gpt-4o", EnvironmentSettings.Optional(Env(("EVAL_MODEL", "gpt-4o")), "fallback", "EVAL_MODEL"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankIsTreatedAsAbsent(string? value)
    {
        // An undefined Actions variable arrives as "", which the ?? idiom would have kept,
        // producing an empty model name and an unhelpful provider error.
        Assert.Equal(
            "gpt-4o-mini",
            EnvironmentSettings.Optional(Env(("EVAL_MODEL", value)), "gpt-4o-mini", "EVAL_MODEL"));
    }

    [Fact]
    public void TheFirstNonBlankNameWins()
    {
        string key = EnvironmentSettings.Required(
            Env(("JUDGE_API_KEY", "judge"), ("EVAL_API_KEY", "eval")),
            "JUDGE_API_KEY", "EVAL_API_KEY");

        Assert.Equal("judge", key);
    }

    [Fact]
    public void ABlankFirstNameFallsThroughToTheNext()
    {
        // The judge secret is not configured, so the candidate key must be used instead.
        string key = EnvironmentSettings.Required(
            Env(("JUDGE_API_KEY", ""), ("EVAL_API_KEY", "eval")),
            "JUDGE_API_KEY", "EVAL_API_KEY");

        Assert.Equal("eval", key);
    }

    [Fact]
    public void ABlankJudgeEndpointStillFallsBackToTheSharedEndpoint()
    {
        // The dangerous one: with ?? this returned "" and the judge quietly used the provider
        // default, producing results from a different endpoint than the one configured.
        string? endpoint = EnvironmentSettings.OptionalOrNull(
            Env(("JUDGE_ENDPOINT", ""), ("EVAL_ENDPOINT", "https://custom.example/v1")),
            "JUDGE_ENDPOINT", "EVAL_ENDPOINT");

        Assert.Equal("https://custom.example/v1", endpoint);
    }

    [Fact]
    public void NoEndpointConfiguredReturnsNullSoTheProviderDefaultIsUsedDeliberately()
    {
        Assert.Null(EnvironmentSettings.OptionalOrNull(
            Env(("JUDGE_ENDPOINT", ""), ("EVAL_ENDPOINT", "  ")), "JUDGE_ENDPOINT", "EVAL_ENDPOINT"));
    }

    [Fact]
    public void AMissingRequiredValueNamesEveryVariableItLookedFor()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            EnvironmentSettings.Required(Env(("EVAL_API_KEY", "")), "EVAL_API_KEY", "OPENAI_API_KEY"));

        Assert.Contains("EVAL_API_KEY", error.Message, StringComparison.Ordinal);
        Assert.Contains("OPENAI_API_KEY", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AKeyStoredUnderTheAlternativeNameIsFound()
    {
        // The workflow passes both EVAL_API_KEY and OPENAI_API_KEY; only one is usually set.
        Assert.Equal(
            "sk-test",
            EnvironmentSettings.Required(
                Env(("EVAL_API_KEY", ""), ("OPENAI_API_KEY", "sk-test")),
                "EVAL_API_KEY", "OPENAI_API_KEY"));
    }
}
