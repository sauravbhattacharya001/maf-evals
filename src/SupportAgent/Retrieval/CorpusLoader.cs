using System.Text;

namespace SupportAgent.Retrieval;

/// <summary>
/// Loads markdown files and splits them on level-two headings. Heading-based chunking keeps
/// each chunk topically coherent, which matters because the retrieval evaluator scores whether
/// the retrieved chunks are relevant, not merely whether the right file was found.
/// </summary>
public static class CorpusLoader
{
        /// <summary>Loads several corpora as one, used to add an adversarial corpus to the real one.</summary>
    public static IReadOnlyList<CorpusChunk> Load(IEnumerable<string> directories) =>
        directories.SelectMany(Load).ToArray();

    public static IReadOnlyList<CorpusChunk> Load(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Corpus directory not found: {directory}");
        }

        List<CorpusChunk> chunks = [];

        foreach (string path in Directory.EnumerateFiles(directory, "*.md").OrderBy(path => path, StringComparer.Ordinal))
        {
            chunks.AddRange(Split(Path.GetFileNameWithoutExtension(path), File.ReadAllLines(path)));
        }

        if (chunks.Count == 0)
        {
            throw new InvalidDataException($"No corpus chunks found in {directory}.");
        }

        return chunks;
    }

    internal static IEnumerable<CorpusChunk> Split(string document, IReadOnlyList<string> lines)
    {
        string? title = null;
        StringBuilder body = new();
        int index = 0;

        foreach (string line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (title is not null)
                {
                    yield return Build(document, ++index, title, body);
                }

                title = line[3..].Trim();
                body.Clear();
                continue;
            }

            if (title is not null)
            {
                body.AppendLine(line);
            }
        }

        if (title is not null)
        {
            yield return Build(document, ++index, title, body);
        }
    }

    private static CorpusChunk Build(string document, int index, string title, StringBuilder body) =>
        new($"{document}#{index}", title, body.ToString().Trim());
}

