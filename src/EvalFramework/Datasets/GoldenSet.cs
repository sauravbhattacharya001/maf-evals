using System.Text.Json;

namespace EvalFramework.Datasets;

/// <summary>Loads golden cases from JSONL so diffs stay reviewable one case per line.</summary>
public static class GoldenSet
{
    public static IReadOnlyList<GoldenCase> Load(string path)
    {
        List<GoldenCase> cases = [];
        int lineNumber = 0;

        foreach (string line in File.ReadLines(path))
        {
            lineNumber++;
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            GoldenCase parsed = JsonSerializer.Deserialize<GoldenCase>(trimmed, JsonDefaults.Options)
                ?? throw new InvalidDataException($"{path}:{lineNumber} is not a valid golden case.");

            cases.Add(parsed);
        }

        string[] duplicates = cases.GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        if (duplicates.Length > 0)
        {
            throw new InvalidDataException($"Duplicate case ids in {path}: {string.Join(", ", duplicates)}");
        }

        if (cases.Count == 0)
        {
            throw new InvalidDataException($"No golden cases found in {path}.");
        }

        return cases;
    }
}
