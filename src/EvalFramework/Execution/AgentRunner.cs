using System.Diagnostics;
using EvalFramework.Datasets;
using EvalFramework.Retrieval;
using EvalFramework.Rules;
using EvalFramework.Statistics;
using Microsoft.Agents.AI;

namespace EvalFramework.Execution;

/// <summary>Tier 1 telemetry captured for a single run.</summary>
public sealed record RunTelemetry(
    RetrievalTrace? Retrieval,
    int Attempts,
    IReadOnlyList<string> RejectedToolCalls);

/// <summary>
/// Supplies Tier 1 telemetry to the runner.
/// </summary>
/// <remarks>
/// An interface rather than a direct dependency, so the eval framework never has to reference the
/// agent project. The runner stays usable against any agent that can report its guardrail activity.
/// </remarks>
public interface IRunTelemetrySource
{
    void Reset();

    RunTelemetry Capture();
}

/// <summary>
/// Executes golden cases against a live agent and records everything needed downstream.
/// </summary>
/// <remarks>
/// Shared by Tier 2 and Tier 3. Tier 2 uses a single repetition because it gates a pull request;
/// Tier 3 uses many because it is measuring reliability rather than blocking a merge.
/// </remarks>
public sealed class AgentRunner(AIAgent agent, string model, IRunTelemetrySource? telemetry = null)
{
    public async Task<RunArtifact> RunAsync(
        IReadOnlyList<GoldenCase> cases,
        int repetitions,
        string tier,
        string datasetPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cases);

        if (repetitions < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(repetitions), "repetitions must be at least 1.");
        }

        List<ResponseRecord> responses = [];

        for (int repetition = 1; repetition <= repetitions; repetition++)
        {
            foreach (GoldenCase goldenCase in cases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                responses.Add(await RunOnceAsync(goldenCase, repetition, progress, cancellationToken)
                    .ConfigureAwait(false));
            }
        }

        return Build(cases, responses, repetitions, tier, model, datasetPath);
    }

    private async Task<ResponseRecord> RunOnceAsync(
        GoldenCase goldenCase,
        int repetition,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        telemetry?.Reset();
        long start = Stopwatch.GetTimestamp();

        string text;
        bool blocked = false;

        try
        {
            AgentResponse response = await agent
                .RunAsync(goldenCase.Query, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            text = response.Text ?? string.Empty;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A Tier 1 block is a legitimate outcome to measure, not a crash to hide.
            blocked = true;
            text = string.Empty;
            progress?.Report($"{goldenCase.Id}: blocked by guardrails ({error.Message})");
        }

        double latencyMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        RunTelemetry captured = telemetry?.Capture() ?? new RunTelemetry(null, 1, []);
        RuleReport rules = ResponseRules.Evaluate(goldenCase.ToRuleSet(), text);

        progress?.Report(
            $"rep {repetition} {goldenCase.Id}: {(rules.Passed && !blocked ? "pass" : "fail")} " +
            $"({latencyMs:F0} ms, {captured.Attempts} attempt(s))");

        return new ResponseRecord
        {
            CaseId = goldenCase.Id,
            Query = goldenCase.Query,
            Repetition = repetition,
            Response = text,
            LatencyMs = latencyMs,
            Rules = rules,
            Retrieval = captured.Retrieval,
            Attempts = captured.Attempts,
            RejectedToolCalls = captured.RejectedToolCalls,
            Blocked = blocked
        };
    }

    /// <summary>Assembles an artifact from recorded responses. Shared with offline tests.</summary>
    public static RunArtifact Build(
        IReadOnlyList<GoldenCase> cases,
        IReadOnlyList<ResponseRecord> responses,
        int repetitions,
        string tier,
        string model,
        string datasetPath)
    {
        int totalPasses = responses.Count(record => record.Rules.Passed && !record.Blocked);
        ConfidenceInterval overall = Wilson.Interval(totalPasses, responses.Count);

        return new RunArtifact
        {
            RunId = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("n")[..6]}",
            Tier = tier,
            TimestampUtc = DateTimeOffset.UtcNow,
            Model = model,
            DatasetPath = datasetPath,
            Repetitions = repetitions,
            OverallPassRate = responses.Count == 0 ? 0d : (double)totalPasses / responses.Count,
            OverallLowerBound = overall.Lower,
            OverallUpperBound = overall.Upper,
            MeanLatencyMs = responses.Count == 0 ? 0d : responses.Average(record => record.LatencyMs),
            Cases = RunAnalyzer.Summarize(cases, responses),
            Responses = responses
        };
    }
}
