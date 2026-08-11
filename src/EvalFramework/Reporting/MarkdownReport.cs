using System.Text;
using EvalFramework.Execution;
using EvalFramework.RagTriad;
using EvalFramework.Statistics;

namespace EvalFramework.Reporting;

/// <summary>Human-readable summaries for pull request comments and CI logs.</summary>
public static class MarkdownReport
{
    public static string ForTier2(Tier2Result result)
    {
        ArgumentNullException.ThrowIfNull(result);

        RunArtifact run = result.Run;
        StringBuilder builder = new();

        builder.AppendLine($"# Tier 2 gate {result.Verdict}");
        builder.AppendLine();
        builder.AppendLine($"- Run: `{run.RunId}`");
        builder.AppendLine($"- Model: `{run.Model}`");
        builder.AppendLine($"- Cases: {run.Cases.Count}, rule pass rate {run.OverallPassRate:P0}");
        builder.AppendLine($"- Mean latency: {run.MeanLatencyMs:F0} ms");
        builder.AppendLine();

        if (result.Triad.Count > 0)
        {
            builder.AppendLine("| Case | Retrieval | Groundedness | Relevance |");
            builder.AppendLine("| --- | --- | --- | --- |");

            foreach (TriadResult triad in result.Triad)
            {
                builder.AppendLine(
                    $"| {triad.CaseId} | {Cell(triad, TriadMetrics.Retrieval, result.Thresholds)} " +
                    $"| {Cell(triad, TriadMetrics.Groundedness, result.Thresholds)} " +
                    $"| {Cell(triad, TriadMetrics.Relevance, result.Thresholds)} |");
            }

            builder.AppendLine();
            builder.AppendLine("Metrics marked advisory are reported but never block; see judge calibration.");
            builder.AppendLine();
        }

        int retried = run.Responses.Count(record => record.Attempts > 1);
        int rejected = run.Responses.Sum(record => record.RejectedToolCalls.Count);
        builder.AppendLine($"Tier 1 activity: {retried} response retry(ies), {rejected} tool call(s) rejected.");

        if (result.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Warnings");
            foreach (string warning in result.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        if (!result.Passed)
        {
            builder.AppendLine();
            builder.AppendLine("## Blocking violations");
            foreach (GateViolation violation in result.Violations)
            {
                builder.AppendLine($"- **{violation.Gate}**: {violation.Detail}");
            }
        }

        return builder.ToString();
    }

    public static string ForRun(RunArtifact run, GateReport gates)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(gates);

        StringBuilder builder = new();
        builder.AppendLine($"# {run.Tier} run {run.RunId}");
        builder.AppendLine();
        builder.AppendLine($"- Model: `{run.Model}`");
        builder.AppendLine($"- Repetitions: {run.Repetitions}");
        builder.AppendLine($"- Overall pass rate: **{run.OverallPassRate:P1}** " +
            $"(95% CI {run.OverallLowerBound:P1} to {run.OverallUpperBound:P1})");
        builder.AppendLine($"- Mean latency: {run.MeanLatencyMs:F0} ms");
        builder.AppendLine($"- Gates: **{(gates.Passed ? "PASS" : "FAIL")}**");
        builder.AppendLine();
        builder.AppendLine("| Case | Critical | Pass rate | 95% CI | Flaky | Top failures |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- |");

        foreach (CaseStatistics statistics in run.Cases)
        {
            string failures = statistics.TopFailures.Count == 0 ? "-" : string.Join(", ", statistics.TopFailures);
            builder.AppendLine(
                $"| {statistics.CaseId} | {(statistics.Critical ? "yes" : "no")} | " +
                $"{statistics.PassRate:P0} ({statistics.Passes}/{statistics.Trials}) | " +
                $"{statistics.LowerBound:P0}-{statistics.UpperBound:P0} | " +
                $"{(statistics.Flaky ? "yes" : "no")} | {failures} |");
        }

        if (!gates.Passed)
        {
            builder.AppendLine();
            builder.AppendLine("## Gate violations");
            foreach (GateViolation violation in gates.Violations)
            {
                builder.AppendLine($"- **{violation.Gate}**: {violation.Detail}");
            }
        }

        return builder.ToString();
    }

    private static string Cell(TriadResult triad, string metric, TriadThresholds thresholds)
    {
        TriadScore? score = triad.Scores.FirstOrDefault(item => item.Metric == metric);

        if (score is null)
        {
            return "-";
        }

        bool blocking = thresholds.For(metric).Blocking;

        return $"{score.Score?.ToString("F1") ?? "n/a"} {Marker(score.Verdict, blocking)}";
    }

    private static string Marker(TriadVerdict verdict, bool blocking) => verdict switch
    {
        TriadVerdict.Pass => "ok",
        TriadVerdict.Warn => "warn",
        TriadVerdict.Fail => blocking ? "FAIL" : "low (advisory)",
        _ => "?"
    };
}

