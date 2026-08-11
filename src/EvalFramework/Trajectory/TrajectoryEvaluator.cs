using EvalFramework.Execution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;

namespace EvalFramework.Trajectory;

/// <summary>
/// Judges how the agent reasoned, not merely what it finally said.
/// </summary>
/// <remarks>
/// <para>
/// Three questions the RAG triad cannot answer, because it only sees the final answer and the
/// retrieved context:
/// </para>
/// <list type="bullet">
///   <item>Intent resolution: did the agent work out what the customer actually wanted?</item>
///   <item>Task adherence: did it follow its instructions and the tools it was given?</item>
///   <item>Tool call accuracy: were the calls it made appropriate and correctly parameterised?</item>
/// </list>
/// <para>
/// An agent can produce a well-grounded, relevant answer by luck: guessing without checking, calling
/// a tool it did not need, or ignoring what a tool returned. Those are failures of reasoning that
/// show up only in the path, which is why this runs over the recorded trajectory.
/// </para>
/// <para>
/// These evaluators are marked experimental in the quality library. They are used here for trend
/// reporting on a schedule, never to gate a merge, so an API change is an inconvenience rather than
/// a broken pipeline.
/// </para>
/// </remarks>
public sealed class TrajectoryEvaluator(
    IChatClient judgeClient,
    IReadOnlyList<AITool> toolDefinitions,
    TimeSpan? perCallTimeout = null)
{
    public async Task<TrajectoryResult> EvaluateAsync(
        ResponseRecord record,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        ChatConfiguration configuration = new(judgeClient);

        IReadOnlyList<ChatMessage> conversation =
            Execution.Trajectory.ToChatMessages(record.Query, record.Trajectory);

        // The agent's "response" is every message it produced, not merely the last one. Passing only
        // the closing text hid the tool calls in earlier turns, and tool call accuracy scored null
        // because it inspects calls present in the response it is given.
        ChatMessage[] history = conversation.Take(1).ToArray();
        ChatMessage[] produced = conversation.Skip(1).ToArray();

        ChatResponse response = produced.Length > 0
            ? new ChatResponse(produced)
            : new ChatResponse(new ChatMessage(ChatRole.Assistant, record.Response));

        List<TrajectoryScore> scores = [];

        scores.AddRange(await ScoreAsync(
            new IntentResolutionEvaluator(),
            TrajectoryMetrics.IntentResolution,
            history,
            response,
            configuration,
            [new IntentResolutionEvaluatorContext(toolDefinitions)],
            cancellationToken).ConfigureAwait(false));

        scores.AddRange(await ScoreAsync(
            new TaskAdherenceEvaluator(),
            TrajectoryMetrics.TaskAdherence,
            history,
            response,
            configuration,
            [new TaskAdherenceEvaluatorContext(toolDefinitions)],
            cancellationToken).ConfigureAwait(false));

        // Only meaningful when the agent actually reached for a tool.
        if (record.ToolCalls.Count > 0)
        {
            scores.AddRange(await ScoreAsync(
                new ToolCallAccuracyEvaluator(),
                TrajectoryMetrics.ToolCallAccuracy,
                history,
                response,
                configuration,
                [new ToolCallAccuracyEvaluatorContext(toolDefinitions)],
                cancellationToken).ConfigureAwait(false));
        }

        progress?.Report($"judged trajectory {record.CaseId} rep {record.Repetition}");

        return new TrajectoryResult(record.CaseId, record.Repetition, scores);
    }

    public async Task<IReadOnlyList<TrajectoryResult>> EvaluateManyAsync(
        IReadOnlyList<ResponseRecord> records,
        int maxConcurrency = 4,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);

        if (maxConcurrency < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency), "maxConcurrency must be at least 1.");
        }

        TrajectoryResult[] results = new TrajectoryResult[records.Count];
        using SemaphoreSlim gate = new(maxConcurrency);

        await Task.WhenAll(records.Select(async (record, index) =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                results[index] = await EvaluateAsync(record, progress, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        return results;
    }

    /// <summary>
    /// Reads a score from either metric shape.
    /// </summary>
    /// <remarks>
    /// Intent resolution and task adherence return a numeric rating, while tool call accuracy
    /// returns a boolean. Unwrapping only the numeric shape silently dropped every tool score while
    /// still recording the judge's explanation, which made a working evaluator look broken.
    /// </remarks>
    private static double? ScoreOf(EvaluationMetric metric) => metric switch
    {
        NumericMetric numeric => numeric.Value,
        BooleanMetric boolean => boolean.Value is bool value ? (value ? 1d : 0d) : null,
        _ => null
    };

    private async Task<IEnumerable<TrajectoryScore>> ScoreAsync(
        IEvaluator evaluator,
        string metricName,
        IEnumerable<ChatMessage> history,
        ChatResponse response,
        ChatConfiguration configuration,
        IEnumerable<EvaluationContext> context,
        CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            if (perCallTimeout is TimeSpan timeout)
            {
                linked.CancelAfter(timeout);
            }

            EvaluationResult result = await evaluator
                .EvaluateAsync(history, response, configuration, context, linked.Token)
                .ConfigureAwait(false);

            return result.Metrics.Select(entry => new TrajectoryScore(
                entry.Key,
                ScoreOf(entry.Value),
                entry.Value.Reason));
        }
        catch (Exception error) when (error is not OperationCanceledException
            || !cancellationToken.IsCancellationRequested)
        {
            // One judge failure costs one metric, never the surrounding run.
            return [new TrajectoryScore(metricName, null, $"judge call failed: {error.Message}")];
        }
    }
}
