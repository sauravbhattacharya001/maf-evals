namespace EvalRunner;

/// <summary>
/// Loads environment variables from a gitignored <c>.env.local</c> at the repository root.
/// </summary>
/// <remarks>
/// Convenience for local runs only. Real environment variables always win, so CI, where secrets
/// arrive through the environment, is unaffected by whatever a developer has on disk.
/// </remarks>
public static class DotEnv
{
    public const string FileName = ".env.local";

    public static void Load(string? path = null)
    {
        path ??= Path.Combine(RepoPaths.Root, FileName);

        if (!File.Exists(path))
        {
            return;
        }

        foreach (string line in File.ReadLines(path))
        {
            string trimmed = line.Trim();

            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            int separator = trimmed.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            string key = trimmed[..separator].Trim();
            string value = trimmed[(separator + 1)..].Trim().Trim('"', '\'');

            // Do not override anything the environment already provides.
            if (!string.IsNullOrEmpty(key)
                && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }
}
