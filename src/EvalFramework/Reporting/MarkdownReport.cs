using System.Text;
using EvalFramework.Execution;
using EvalFramework.Judging;
using EvalFramework.Statistics;

namespace EvalFramework.Reporting;

/// <summary>Human-readable summary intended for pull request comments and CI logs.</summary>
public static class MarkdownReport
{
    public static string ForRun(RunArtifact run, GateReport gates)
    {
        StringBuilder builder = new();
        builder.AppendLine($"# Tier 2 run {run.RunId}");
        builder.AppendLine();
        builder.AppendLine($"- Model: `{run.Model}`");
        builder.AppendLine($"- Dataset: `{run.DatasetPath}`");
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

    public static string ForJudge(JudgeArtifact artifact)
    {
        StringBuilder builder = new();
        builder.AppendLine($"# Tier 3 judgement of run {artifact.SourceRunId}");
        builder.AppendLine();
        builder.AppendLine($"- Judge model: `{artifact.JudgeModel}`");
        builder.AppendLine($"- Rubric: `{artifact.RubricVersion}`");
        builder.AppendLine($"- Responses judged: {artifact.Judged.Count}");
        builder.AppendLine();
        builder.AppendLine("| Metric | Mean | Min | Scored |");
        builder.AppendLine("| --- | --- | --- | --- |");

        foreach (MetricSummary metric in artifact.Summary)
        {
            builder.AppendLine($"| {metric.Name} | {metric.Mean:F2} | {metric.Min:F2} | {metric.Scored} |");
        }

        return builder.ToString();
    }
}
