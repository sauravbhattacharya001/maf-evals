namespace EvalRunner;

/// <summary>Minimal argument parsing. A CLI dependency is not worth the weight here.</summary>
public sealed class CommandLine(string[] args)
{
    /// <summary>Options and flags each command accepts. Anything else is a mistake.</summary>
    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal)
    {
        ["rules"] = [],
        ["tier2"] = ["--no-triad"],
        ["tier3"] = ["--run"],
        ["safety"] = [],
        ["calibrate"] = ["--repeat", "--case", "--semantic"],
        ["incident"] = ["--trace", "--judge"],
        ["retrieve"] = ["--query", "--top"],
        ["report"] = ["--run"],
        ["help"] = []
    };

    public string Command { get; } = args.Length == 0 ? "help" : args[0];

    /// <summary>
    /// Rejects options the command does not know.
    /// </summary>
    /// <remarks>
    /// Silently ignoring an unknown option is how a renamed flag goes unnoticed. When incident
    /// replay moved out of Tier 3, CI still passed <c>tier3 --incident PATH</c>. The flag was
    /// dropped without complaint and a full Tier 3 run started against the live model instead.
    /// An unrecognised option now stops the command.
    /// </remarks>
    public void Validate()
    {
        if (!Allowed.TryGetValue(Command, out string[]? allowed))
        {
            return;
        }

        string[] unknown = args.Skip(1)
            .Where(token => token.StartsWith("--", StringComparison.Ordinal))
            .Where(token => !allowed.Contains(token, StringComparer.Ordinal))
            .ToArray();

        if (unknown.Length > 0)
        {
            throw new InvalidOperationException(
                $"'{Command}' does not accept {string.Join(", ", unknown)}. "
                + (allowed.Length == 0
                    ? "It takes no options."
                    : $"It accepts {string.Join(", ", allowed)}."));
        }
    }

    public string? Option(string name)
    {
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    public int? IntOption(string name) =>
        int.TryParse(Option(name), out int value) ? value : null;

    public bool HasFlag(string name) => args.Skip(1).Contains(name, StringComparer.Ordinal);

}

/// <summary>Locates repository assets regardless of the working directory.</summary>
public static class RepoPaths
{
    public static string Root { get; } = Find();

    public static string GoldenSet => Path.Combine(Root, "datasets", "support-golden-set.jsonl");

    public static string PositiveFixtures => Path.Combine(Root, "datasets", "positive-fixtures.json");

    public static string NegativeFixtures => Path.Combine(Root, "datasets", "negative-fixtures.json");

    public static string Config => Path.Combine(Root, "config", "eval-config.json");

    public static string Corpus => Path.Combine(Root, "corpus");

    public static string Calibration => Path.Combine(Root, "datasets", "judge-calibration.jsonl");

    public static string AdversarialCorpus => Path.Combine(Root, "corpus-adversarial");

    public static string AdversarialSet => Path.Combine(Root, "datasets", "adversarial-set.jsonl");

    public static string RunsDirectory => Path.Combine(Root, "artifacts", "runs");

    public static string CacheDirectory => Path.Combine(Root, "artifacts", "cache");

    public static string? LatestRun()
    {
        if (!Directory.Exists(RunsDirectory))
        {
            return null;
        }

        return Directory.EnumerateFiles(RunsDirectory, "tier*.json")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string Find()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "datasets")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}







