using System.Text.Json.Serialization;
using EvalFramework.Execution;
using EvalFramework.RagTriad;
using EvalFramework.Rules;

namespace EvalFramework.Calibration;

/// <summary>Result of running the judge across the labelled calibration set.</summary>
public sealed record CalibrationReport
{
    public const string CurrentSchemaVersion = "calibration/v1";

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("timestampUtc")]
    public required DateTimeOffset TimestampUtc { get; init; }

    [JsonPropertyName("judgeModel")]
    public required string JudgeModel { get; init; }

    [JsonPropertyName("agreement")]
    public required IReadOnlyList<MetricAgreement> Agreement { get; init; }

    /// <summary>Populated only when the judge was run more than once per case.</summary>
    [JsonPropertyName("consistency")]
    public IReadOnlyList<ConsistencySummary> Consistency { get; init; } = [];

    [JsonPropertyName("pairs")]
    public required IReadOnlyList<ScorePair> Pairs { get; init; }
}

/// <summary>Runs the triad over labelled cases and reports agreement with the human scores.</summary>
public sealed class CalibrationRunner(TriadEvaluator evaluator, string judgeModel)
{
    public async Task<CalibrationReport> RunAsync(
        IReadOnlyList<CalibrationCase> cases,
        TriadThresholds thresholds,
        int repetitions = 1,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(thresholds);

        if (repetitions < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(repetitions), "repetitions must be at least 1.");
        }

        List<ScorePair> pairs = [];

        for (int repetition = 1; repetition <= repetitions; repetition++)
        {
            foreach (CalibrationCase item in cases)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ResponseRecord record = new()
                {
                    CaseId = item.Id,
                    Query = item.Query,
                    Repetition = repetition,
                    Response = item.Response,
                    LatencyMs = 0,
                    Rules = new RuleReport([]),
                    Retrieval = item.ToRetrievalTrace()
                };

                TriadResult result = await evaluator
                    .EvaluateAsync(record, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                foreach (string metric in new[]
                         { TriadMetrics.Retrieval, TriadMetrics.Groundedness, TriadMetrics.Relevance })
                {
                    TriadScore? score = result.Scores.FirstOrDefault(s => s.Metric == metric);
                    pairs.Add(new ScorePair(item.Id, metric, item.Labels.For(metric), score?.Score, repetition));
                }

                progress?.Report($"rep {repetition} {item.Id} ({item.Label})");
            }
        }

        // Agreement uses the first pass only, so repeated judging cannot quietly average away a
        // disagreement that a single real run would have hit.
        ScorePair[] firstPass = pairs.Where(pair => pair.Repetition == 1).ToArray();

        MetricAgreement[] agreement =
        [
            AgreementMetrics.Compare(TriadMetrics.Retrieval, firstPass, thresholds.Retrieval),
            AgreementMetrics.Compare(TriadMetrics.Groundedness, firstPass, thresholds.Groundedness),
            AgreementMetrics.Compare(TriadMetrics.Relevance, firstPass, thresholds.Relevance)
        ];

        return new CalibrationReport
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            JudgeModel = judgeModel,
            Agreement = agreement,
            Consistency = repetitions > 1 ? ConsistencyMetrics.Summarize(pairs, thresholds) : [],
            Pairs = pairs
        };
    }
}
