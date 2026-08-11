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
/// <para>
/// Shared by Tier 2 and Tier 3. Tier 2 uses a single repetition because it gates a pull request;
/// Tier 3 uses many because it is measuring reliability rather than blocking a merge.
/// </para>
/// <para>
/// Deliberately sequential. Telemetry is captured through a per-agent recorder that is reset before
/// each invocation, so concurrent runs would interleave one another's retrieval traces and retry
/// counts. Parallelism here would corrupt the evidence to save wall-clock time; the judge, which is
/// stateless, is parallelised instead.
/// </para>
/// </remarks>
public sealed class AgentRunner(
    AIAgent agent,
    string model,
    IRunTelemetrySource? telemetry = null,
    TimeSpan? perRunTimeout = null)
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
        ResponseOutcome outcome = ResponseOutcome.Completed;
        string? error = null;

        try
        {
            // A hung call would otherwise stall an entire scheduled run. The timeout is linked to
            // the caller's token so cancellation still propagates, and a timeout is recorded as
            // Errored rather than as an agent failure.
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            if (perRunTimeout is TimeSpan timeout)
            {
                linked.CancelAfter(timeout);
            }

            AgentResponse response = await agent
                .RunAsync(goldenCase.Query, cancellationToken: linked.Token)
                .ConfigureAwait(false);

            text = response.Text ?? string.Empty;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            outcome = ResponseOutcome.Errored;
            text = string.Empty;
            error = $"timed out after {perRunTimeout?.TotalSeconds:F0}s";
            progress?.Report($"{goldenCase.Id}: ERRORED (timeout)");
        }
        catch (Exception blocked) when (blocked is IRuleBlockedException)
        {
            // A Tier 1 block is a legitimate outcome to measure.
            outcome = ResponseOutcome.Blocked;
            text = string.Empty;
            error = blocked.Message;
            progress?.Report($"{goldenCase.Id}: blocked by guardrails");
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // Anything else is missing data. Counting it as a failure would let an API outage
            // masquerade as an agent regression.
            outcome = ResponseOutcome.Errored;
            text = string.Empty;
            error = failure.Message;
            progress?.Report($"{goldenCase.Id}: ERRORED ({failure.GetType().Name}: {failure.Message})");
        }

        double latencyMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        RunTelemetry captured = telemetry?.Capture() ?? new RunTelemetry(null, 1, []);
        RuleReport rules = ResponseRules.Evaluate(goldenCase.ToRuleSet(), text);

        if (outcome != ResponseOutcome.Errored)
        {
            progress?.Report(
                $"rep {repetition} {goldenCase.Id}: " +
                $"{(rules.Passed && outcome == ResponseOutcome.Completed ? "pass" : "fail")} " +
                $"({latencyMs:F0} ms, {captured.Attempts} attempt(s))");
        }

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
            Outcome = outcome,
            Error = error
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
        // Errored runs are missing data: they are excluded from the denominator rather than
        // counted as failures, and surfaced separately so a silent outage cannot be mistaken
        // for a healthy run with a low score.
        ResponseRecord[] counted = responses.Where(record => record.Counts).ToArray();
        int totalPasses = counted.Count(record => record.Rules.Passed && !record.Blocked);
        ConfidenceInterval overall = Wilson.Interval(totalPasses, counted.Length);

        return new RunArtifact
        {
            RunId = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("n")[..6]}",
            Tier = tier,
            TimestampUtc = DateTimeOffset.UtcNow,
            Model = model,
            DatasetPath = datasetPath,
            Repetitions = repetitions,
            ErroredCount = responses.Count - counted.Length,
            OverallPassRate = counted.Length == 0 ? 0d : (double)totalPasses / counted.Length,
            OverallLowerBound = overall.Lower,
            OverallUpperBound = overall.Upper,
            MeanLatencyMs = counted.Length == 0 ? 0d : counted.Average(record => record.LatencyMs),
            Cases = RunAnalyzer.Summarize(cases, responses),
            Responses = responses
        };
    }
}
