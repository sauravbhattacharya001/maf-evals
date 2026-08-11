using System.Diagnostics;
using EvalFramework.Datasets;
using EvalFramework.Deterministic;
using EvalFramework.Statistics;
using Microsoft.Agents.AI;

namespace EvalFramework.Execution;

/// <summary>
/// Tier 2. Runs the candidate agent repeatedly over the golden set and records every
/// response. Repetition is the point: one pass per case cannot distinguish a reliable
/// agent from a lucky one.
/// </summary>
public sealed class GoldenSetRunner(AIAgent agent, string model)
{
    public async Task<RunArtifact> RunAsync(
        IReadOnlyList<GoldenCase> cases,
        EvalConfig config,
        string datasetPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(config);

        if (config.Repetitions < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(config), "repetitions must be at least 1.");
        }

        List<ResponseRecord> responses = [];

        for (int repetition = 1; repetition <= config.Repetitions; repetition++)
        {
            foreach (GoldenCase goldenCase in cases)
            {
                cancellationToken.ThrowIfCancellationRequested();

                long start = Stopwatch.GetTimestamp();
                AgentResponse response = await agent
                    .RunAsync(goldenCase.Query, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                double latencyMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

                string text = response.Text ?? string.Empty;
                DeterministicResult deterministic = DeterministicEvaluator.Evaluate(goldenCase, text);

                responses.Add(new ResponseRecord
                {
                    CaseId = goldenCase.Id,
                    Query = goldenCase.Query,
                    Repetition = repetition,
                    Response = text,
                    LatencyMs = latencyMs,
                    Deterministic = deterministic
                });

                progress?.Report(
                    $"rep {repetition}/{config.Repetitions} {goldenCase.Id}: " +
                    $"{(deterministic.Passed ? "pass" : "fail")} ({latencyMs:F0} ms)");
            }
        }

        return Build(cases, responses, config, model, datasetPath);
    }

    /// <summary>Assembles an artifact from recorded responses. Shared with offline tests.</summary>
    public static RunArtifact Build(
        IReadOnlyList<GoldenCase> cases,
        IReadOnlyList<ResponseRecord> responses,
        EvalConfig config,
        string model,
        string datasetPath)
    {
        int totalPasses = responses.Count(record => record.Deterministic.Passed);
        ConfidenceInterval overall = Wilson.Interval(totalPasses, responses.Count);

        return new RunArtifact
        {
            RunId = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("n")[..6]}",
            TimestampUtc = DateTimeOffset.UtcNow,
            Model = model,
            DatasetPath = datasetPath,
            Repetitions = config.Repetitions,
            OverallPassRate = responses.Count == 0 ? 0d : (double)totalPasses / responses.Count,
            OverallLowerBound = overall.Lower,
            OverallUpperBound = overall.Upper,
            MeanLatencyMs = responses.Count == 0 ? 0d : responses.Average(record => record.LatencyMs),
            Cases = RunAnalyzer.Summarize(cases, responses),
            Responses = responses
        };
    }
}
