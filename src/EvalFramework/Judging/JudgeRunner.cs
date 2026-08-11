using EvalFramework.Execution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;

namespace EvalFramework.Judging;

/// <summary>
/// Tier 3. Scores responses that Tier 2 already recorded, so subjective quality never
/// costs another candidate-agent run and both tiers describe the same responses.
/// </summary>
public sealed class JudgeRunner(IChatClient judgeClient, string judgeModel, string rubricVersion)
{
    public async Task<JudgeArtifact> JudgeAsync(
        RunArtifact run,
        int? samplePerCase = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        IEvaluator evaluator = new CompositeEvaluator(
            new RelevanceEvaluator(),
            new CoherenceEvaluator());

        ChatConfiguration configuration = new(judgeClient);
        List<JudgedResponse> judged = [];

        foreach (ResponseRecord record in SelectResponses(run, samplePerCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            ChatMessage[] messages = [new(ChatRole.User, record.Query)];
            ChatResponse modelResponse = new(new ChatMessage(ChatRole.Assistant, record.Response));

            EvaluationResult result = await evaluator
                .EvaluateAsync(messages, modelResponse, configuration, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            JudgeMetric[] metrics = result.Metrics
                .Select(entry => new JudgeMetric(
                    entry.Key,
                    entry.Value is NumericMetric numeric ? numeric.Value : null,
                    entry.Value.Reason))
                .ToArray();

            judged.Add(new JudgedResponse
            {
                CaseId = record.CaseId,
                Repetition = record.Repetition,
                Metrics = metrics
            });

            progress?.Report($"judged {record.CaseId} rep {record.Repetition}");
        }

        return new JudgeArtifact
        {
            SourceRunId = run.RunId,
            TimestampUtc = DateTimeOffset.UtcNow,
            JudgeModel = judgeModel,
            RubricVersion = rubricVersion,
            Summary = JudgeSummarizer.Summarize(judged),
            Judged = judged
        };
    }

    /// <summary>Judging every repetition is usually wasteful, so sampling is supported.</summary>
    private static IEnumerable<ResponseRecord> SelectResponses(RunArtifact run, int? samplePerCase)
    {
        if (samplePerCase is not int limit || limit <= 0)
        {
            return run.Responses;
        }

        return run.Responses
            .GroupBy(record => record.CaseId, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group.OrderBy(record => record.Repetition).Take(limit));
    }
}
