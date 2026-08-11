using EvalFramework.Execution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;

namespace EvalFramework.RagTriad;

/// <summary>
/// The RAG triad: context relevance, groundedness, and answer relevance.
/// </summary>
/// <remarks>
/// <para>
/// Each metric isolates a different failure. Retrieval catches a bad knowledge base or query,
/// groundedness catches answers invented beyond the retrieved context, and relevance catches
/// answers that are well grounded but do not address the question. A single quality score would
/// blur all three into one unactionable number.
/// </para>
/// <para>
/// Scored against the response and the retrieval trace already captured by the run, so the judge
/// never re-invokes the candidate agent.
/// </para>
/// </remarks>
public sealed class TriadEvaluator(IChatClient judgeClient, TriadThresholds? thresholds = null)
{
    private readonly TriadThresholds _thresholds = thresholds ?? new TriadThresholds();

    public async Task<TriadResult> EvaluateAsync(
        ResponseRecord record,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        ChatConfiguration configuration = new(judgeClient);
        ChatMessage[] messages = [new(ChatRole.User, record.Query)];
        ChatResponse response = new(new ChatMessage(ChatRole.Assistant, record.Response));

        List<TriadScore> scores = [];

        // Retrieval and groundedness are only meaningful when context exists.
        if (record.Retrieval is { Chunks.Count: > 0 } trace)
        {
            scores.AddRange(await ScoreAsync(
                new RetrievalEvaluator(),
                messages,
                response,
                configuration,
                [new RetrievalEvaluatorContext(trace.ChunkTexts)],
                cancellationToken).ConfigureAwait(false));

            scores.AddRange(await ScoreAsync(
                new GroundednessEvaluator(),
                messages,
                response,
                configuration,
                [new GroundednessEvaluatorContext(trace.Combined)],
                cancellationToken).ConfigureAwait(false));
        }

        scores.AddRange(await ScoreAsync(
            new RelevanceEvaluator(),
            messages,
            response,
            configuration,
            null,
            cancellationToken).ConfigureAwait(false));

        progress?.Report($"judged {record.CaseId}");

        return new TriadResult(record.CaseId, scores);
    }

    private async Task<IEnumerable<TriadScore>> ScoreAsync(
        IEvaluator evaluator,
        IEnumerable<ChatMessage> messages,
        ChatResponse response,
        ChatConfiguration configuration,
        IEnumerable<EvaluationContext>? context,
        CancellationToken cancellationToken)
    {
        EvaluationResult result;

        try
        {
            result = await evaluator
                .EvaluateAsync(messages, response, configuration, context, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A judge failure is missing data for one metric. Letting it propagate would discard
            // every result already paid for in this run, and a whole-run retry would pay twice.
            return [new TriadScore(
                MetricNameFor(evaluator),
                null,
                TriadVerdict.NotScored,
                $"judge call failed: {error.Message}")];
        }

        return result.Metrics.Select(entry =>
        {
            double? score = entry.Value is NumericMetric numeric ? numeric.Value : null;

            return new TriadScore(
                entry.Key,
                score,
                _thresholds.For(entry.Key).Classify(score),
                entry.Value.Reason);
        });
    }

    /// <summary>Names the metric a failed evaluator would have produced, so the gap is visible.</summary>
    private static string MetricNameFor(IEvaluator evaluator) => evaluator switch
    {
        RetrievalEvaluator => TriadMetrics.Retrieval,
        GroundednessEvaluator => TriadMetrics.Groundedness,
        RelevanceEvaluator => TriadMetrics.Relevance,
        _ => evaluator.GetType().Name
    };
}
