namespace EvalRunner;

/// <summary>
/// Resolves configuration from the environment.
/// </summary>
/// <remarks>
/// Everything here treats blank as absent. GitHub Actions injects an undefined secret or variable as
/// an empty string rather than omitting it, so <c>??</c> never fires and the empty value wins: a
/// model name becomes "", or a judge endpoint silently falls back to the provider default instead of
/// the configured one. The second failure is the dangerous one, because it produces results from the
/// wrong place rather than an error. The lookup is injected so this is testable without mutating
/// real process state.
/// </remarks>
public static class EnvironmentSettings
{
    public static Func<string, string?> SystemLookup { get; } = Environment.GetEnvironmentVariable;

    /// <summary>First non-blank value among <paramref name="names"/>, or <paramref name="fallback"/>.</summary>
    public static string Optional(Func<string, string?> lookup, string fallback, params string[] names) =>
        FirstNonBlank(lookup, names) ?? fallback;

    /// <summary>First non-blank value among <paramref name="names"/>, or null when none is set.</summary>
    public static string? OptionalOrNull(Func<string, string?> lookup, params string[] names) =>
        FirstNonBlank(lookup, names);

    /// <summary>First non-blank value, or a diagnosable failure naming every variable tried.</summary>
    public static string Required(Func<string, string?> lookup, params string[] names) =>
        FirstNonBlank(lookup, names)
        ?? throw new InvalidOperationException(
            $"Set one of {string.Join(" or ", names)} to run model-backed tiers.");

    private static string? FirstNonBlank(Func<string, string?> lookup, params string[] names)
    {
        foreach (string name in names)
        {
            string? value = lookup(name);

            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
