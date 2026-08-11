namespace EvalRunner;

/// <summary>Minimal argument parsing. A CLI dependency is not worth the weight here.</summary>
public sealed class CommandLine(string[] args)
{
    public string Command { get; } = args.Length == 0 ? "help" : args[0];

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

    public double DoubleOption(string name, double fallback) =>
        double.TryParse(Option(name), out double value) ? value : fallback;
}

/// <summary>Locates repository assets regardless of the working directory.</summary>
public static class RepoPaths
{
    public static string Root { get; } = Find();

    public static string GoldenSet => Path.Combine(Root, "datasets", "support-golden-set.jsonl");

    public static string RecordedResponses => Path.Combine(Root, "datasets", "tier1-recorded-responses.json");

    public static string Config => Path.Combine(Root, "config", "eval-config.json");

    public static string Corpus => Path.Combine(Root, "corpus");

    public static string RunsDirectory => Path.Combine(Root, "artifacts", "runs");

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
