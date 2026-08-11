using System.Text.Json;
using EvalFramework;
using EvalFramework.Calibration;
using EvalFramework.Cost;
using EvalFramework.Datasets;
using EvalFramework.Execution;
using EvalFramework.Incident;
using EvalFramework.RagTriad;
using EvalFramework.Reporting;
using EvalFramework.Rules;
using EvalFramework.Statistics;
using EvalRunner;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SupportAgent;
using SupportAgent.Retrieval;

CommandLine cli = new(args);
DotEnv.Load();

try
{
    return cli.Command switch
    {
        "rules" => Rules(),
        "tier2" => await Tier2Async(cli),
        "tier3" => await Tier3Async(cli),
        "report" => Report(cli),
        "retrieve" => Retrieve(cli),
        "safety" => await SafetyAsync(cli),
        "calibrate" => await CalibrateAsync(cli),
        _ => Help()
    };
}
catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
{
    // A broken artifact or missing configuration is a diagnosable condition, not a stack trace.
    Console.Error.WriteLine($"error: {error.Message}");
    return 2;
}

// Offline check that the rules accept correct output. The negative fixtures, run by the test
// suite, check the other half: that the same rules reject incorrect output.
static int Rules()
{
    IReadOnlyList<GoldenCase> cases = GoldenSet.Load(RepoPaths.GoldenSet);
    PositiveFixtureSet positives = PositiveFixtureSet.Load(RepoPaths.PositiveFixtures);

    Dictionary<string, string> lookup = positives.Fixtures
        .ToDictionary(item => item.CaseId, item => item.Response, StringComparer.OrdinalIgnoreCase);

    int failures = 0;
    Console.WriteLine($"Rule checks over {cases.Count} known-good responses\n");

    foreach (GoldenCase goldenCase in cases)
    {
        if (!lookup.TryGetValue(goldenCase.Id, out string? response))
        {
            Console.WriteLine($"[FAIL] {goldenCase.Id}: no positive fixture");
            failures++;
            continue;
        }

        RuleReport report = ResponseRules.Evaluate(goldenCase.ToRuleSet(), response);
        Console.WriteLine($"[{(report.Passed ? "PASS" : "FAIL")}] {goldenCase.Id}");

        foreach (CheckResult check in report.Failures)
        {
            Console.WriteLine($"        {check.Severity.ToString().ToUpperInvariant()} {check.Name}: {check.Detail}");
        }

        if (!report.Passed)
        {
            failures++;
        }
    }

    Console.WriteLine($"\n{cases.Count - failures}/{cases.Count} cases passed");
    return failures == 0 ? 0 : 1;
}

// Offline retrieval inspection. Authoring a case means knowing what the retriever returns for it.
static int Retrieve(CommandLine cli)
{
    string query = cli.Option("--query")
        ?? throw new InvalidOperationException("Pass --query \"...\" to inspect retrieval.");

    KeywordRetriever retriever = KeywordRetriever.FromDirectory(RepoPaths.Corpus);
    EvalFramework.Retrieval.RetrievalTrace trace = retriever.Retrieve(query, cli.IntOption("--top") ?? 5);

    Console.WriteLine($"query: {query}\n");

    foreach (EvalFramework.Retrieval.RetrievedChunk chunk in trace.Chunks)
    {
        Console.WriteLine($"  {chunk.Score,6:F2}  {chunk.Id,-20} {chunk.Title}");
    }

    if (trace.Chunks.Count == 0)
    {
        Console.WriteLine("  (nothing retrieved)");
    }

    return 0;
}

// Tier 2: the pull-request gate. One pass per case, rules plus the RAG triad.
static async Task<int> Tier2Async(CommandLine cli)
{
    IReadOnlyList<GoldenCase> cases = GoldenSet.Load(RepoPaths.GoldenSet);
    EvalConfig config = LoadConfig();
    int repetitions = cli.IntOption("--repetitions") ?? config.Tier2Repetitions;

    (AIAgent agent, IRunTelemetrySource telemetry, string model, UsageTracker usage) = BuildAgent(repetitions);

    Console.WriteLine($"Tier 2: {cases.Count} cases x {repetitions} on {model}\n");
    Progress<string> progress = new(line => Console.WriteLine($"  {line}"));

    RunArtifact run = await new AgentRunner(agent, model, telemetry, TimeSpan.FromSeconds(config.CallTimeoutSeconds), usage, config.Pricing)
        .RunAsync(cases, repetitions, tier: "tier2", RepoPaths.GoldenSet, progress);

    List<TriadResult> triad = [];
    bool triadEvaluated = !cli.HasFlag("--no-triad");
    UsageTracker? judgeTracker = null;

    if (triadEvaluated)
    {
        (IChatClient judgeClient, string judgeModel, UsageTracker judgeUsage) = ModelFactory.CreateJudge();
        judgeTracker = judgeUsage;
        Console.WriteLine($"\nJudging with {judgeModel}");

        TriadEvaluator evaluator = new(
            judgeClient, config.Triad, TimeSpan.FromSeconds(config.CallTimeoutSeconds));

        ResponseRecord[] judgeable = run.Responses.Where(record => record.Counts && !record.Blocked).ToArray();
        triad.AddRange(await evaluator.EvaluateManyAsync(judgeable, config.JudgeConcurrency, progress));
    }

    Tier2Result result = Tier2Gate.Apply(
        run, cases, triad, config.Triad, triadEvaluated,
        triadEvaluated ? judgeTracker?.Snapshot(config.Pricing) : null,
        config.MaxRunCostUsd);
    string path = Save($"tier2-{run.RunId}.json", result);

    Console.WriteLine();
    Console.WriteLine(MarkdownReport.ForTier2(result));
    Console.WriteLine($"artifact: {path}");

    return result.Passed ? 0 : 1;
}

// Adversarial suite. The corpus is deliberately poisoned so indirect prompt injection is exercised
// the way it actually reaches a RAG agent: through retrieved content, not through the user turn.
static async Task<int> SafetyAsync(CommandLine cli)
{
    IReadOnlyList<GoldenCase> cases = GoldenSet.Load(RepoPaths.AdversarialSet);
    EvalConfig config = LoadConfig();
    int repetitions = cli.IntOption("--repetitions") ?? 1;

    (IChatClient client, string model, UsageTracker usage) = ModelFactory.CreateCandidate(
        EvalPolicy.ShouldCacheCandidate(repetitions));

    KeywordRetriever retriever = KeywordRetriever.FromDirectories(
        RepoPaths.Corpus, RepoPaths.AdversarialCorpus);

    (AIAgent agent, GuardrailRecorder recorder) = SupportAgentFactory.Create(client, retriever);

    Console.WriteLine($"Safety: {cases.Count} adversarial cases x {repetitions} on {model}\n");
    Progress<string> progress = new(line => Console.WriteLine($"  {line}"));

    RunArtifact run = await new AgentRunner(
            agent, model, new RecorderTelemetrySource(recorder),
            TimeSpan.FromSeconds(config.CallTimeoutSeconds), usage, config.Pricing)
        .RunAsync(cases, repetitions, tier: "safety", RepoPaths.AdversarialSet, progress);

    // Judged by rules only. A refusal is a fact about the text, and a jailbreak that succeeded is
    // visible in what the agent said and did, so a model is not needed to see it.
    Tier2Result result = Tier2Gate.Apply(run, cases, [], config.Triad, triadEvaluated: false);
    string path = Save($"safety-{run.RunId}.json", result);

    Console.WriteLine();
    Console.WriteLine($"# Safety suite {(result.Passed ? "PASSED" : "FAILED")}");
    Console.WriteLine();

    foreach (GoldenCase item in cases)
    {
        ResponseRecord[] records = run.Responses.Where(r => r.CaseId == item.Id).ToArray();
        bool held = records.All(r => r.Rules.Passed && !r.Blocked && !r.Errored)
            && !result.Violations.Any(v => v.Detail.StartsWith(item.Id, StringComparison.Ordinal));

        Console.WriteLine($"  [{(held ? "HELD" : "BREACH")}] {item.Id}: {item.Attack}");
    }

    if (!result.Passed)
    {
        Console.WriteLine();
        foreach (GateViolation violation in result.Violations)
        {
            Console.WriteLine($"  - {violation.Gate}: {violation.Detail}");
        }
    }

    Console.WriteLine();
    Console.WriteLine($"artifact: {path}");

    return result.Passed ? 0 : 1;
}

// Tier 3: scheduled reliability measurement, or forensic replay of one incident.
static async Task<int> Tier3Async(CommandLine cli)
{
    if (cli.Option("--incident") is string incidentPath)
    {
        return await ReplayIncidentAsync(cli, incidentPath);
    }

    IReadOnlyList<GoldenCase> cases = GoldenSet.Load(RepoPaths.GoldenSet);
    EvalConfig config = LoadConfig();
    int repetitions = cli.IntOption("--repetitions") ?? config.Tier3Repetitions;

    (AIAgent agent, IRunTelemetrySource telemetry, string model, UsageTracker usage) = BuildAgent(repetitions);

    Console.WriteLine($"Tier 3: {cases.Count} cases x {repetitions} on {model}\n");
    Progress<string> progress = new(line => Console.WriteLine($"  {line}"));

    RunArtifact run = await new AgentRunner(agent, model, telemetry, TimeSpan.FromSeconds(config.CallTimeoutSeconds), usage, config.Pricing)
        .RunAsync(cases, repetitions, tier: "tier3", RepoPaths.GoldenSet, progress);

    GateReport gates = RunAnalyzer.ApplyGates(run, config);
    string path = Save($"tier3-{run.RunId}.json", run);

    Console.WriteLine();
    Console.WriteLine(MarkdownReport.ForRun(run, gates));
    Console.WriteLine($"artifact: {path}");

    return gates.Passed ? 0 : 1;
}

static int Report(CommandLine cli)
{
    string path = cli.Option("--run")
        ?? RepoPaths.LatestRun()
        ?? throw new InvalidOperationException("No run artifact found.");

    // A Tier 2 artifact already carries its own verdict. Re-judging it with Tier 3's statistical
    // gates would report a single-pass gate as failing simply because one observation per case
    // cannot support a confidence bound.
    if (ArtifactReader.SchemaOf(path) == Tier2Result.CurrentSchemaVersion)
    {
        Console.WriteLine(MarkdownReport.ForTier2(ArtifactReader.ReadTier2(path)));
        return 0;
    }

    RunArtifact run = ArtifactReader.ReadRun(path);
    Console.WriteLine(MarkdownReport.ForRun(run, RunAnalyzer.ApplyGates(run, LoadConfig())));

    return 0;
}

// Replays a captured production trace against today's rules. Fully offline unless a judge is asked for.
static async Task<int> ReplayIncidentAsync(CommandLine cli, string incidentPath)
{
    IncidentTrace trace = IncidentTrace.Load(incidentPath);
    IReadOnlyList<GoldenCase> cases = GoldenSet.Load(RepoPaths.GoldenSet);
    EvalConfig config = LoadConfig();

    GoldenCase? related = cases.FirstOrDefault(item =>
        string.Equals(item.Id, trace.RelatedCaseId, StringComparison.OrdinalIgnoreCase));

    TriadResult? triad = null;

    if (cli.HasFlag("--judge"))
    {
        (IChatClient judgeClient, string judgeModel, UsageTracker judgeUsage) = ModelFactory.CreateJudge();
        Console.WriteLine($"Judging incident with {judgeModel}\n");

        RuleReport rules = ResponseRules.Evaluate(
            related?.ToRuleSet() ?? SupportPolicy.BaselineRules, trace.Response);

        triad = await new TriadEvaluator(judgeClient, config.Triad)
            .EvaluateAsync(IncidentReplay.ToResponseRecord(trace, rules));
    }

    IncidentReport report = IncidentReplay.Replay(trace, related, SupportPolicy.ToolRules, SupportPolicy.BaselineRules, triad);
    string path = Save($"incident-{trace.IncidentId}.json", report);

    Console.WriteLine($"# Incident {report.IncidentId}");
    Console.WriteLine($"Related case: {report.RelatedCaseId ?? "none"}\n");

    foreach (CheckResult failure in report.RuleFailures)
    {
        Console.WriteLine($"  rule      {failure.Severity}: {failure.Name} ({failure.Detail})");
    }

    foreach (string failure in report.ToolFailures)
    {
        Console.WriteLine($"  tool      {failure}");
    }

    foreach (string chunk in report.MissingChunks)
    {
        Console.WriteLine($"  retrieval missing chunk {chunk}");
    }

    Console.WriteLine();
    Console.WriteLine(report.UnexplainedByRules
        ? "No deterministic rule explains this incident. Add a golden case, or a recurrence will not be caught."
        : "Current rules would have caught this incident.");

    Console.WriteLine($"artifact: {path}");

    // Replay is a diagnostic, not a gate, so a caught incident is a success.
    return 0;
}

// Checks the judge against human labels. Without this, thresholds are taste, not measurement.
static async Task<int> CalibrateAsync(CommandLine cli)
{
        IReadOnlyList<CalibrationCase> cases = CalibrationSet.Load(RepoPaths.Calibration);
    EvalConfig config = LoadConfig();

    if (cli.Option("--case") is string only)
    {
        cases = cases.Where(item => item.Id.Equals(only, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    int repetitions = cli.IntOption("--repeat") ?? 1;

    // Repeated judging must bypass the cache, or every repetition replays one stored answer.
    (IChatClient judgeClient, string judgeModel, UsageTracker judgeUsage) = ModelFactory.CreateJudge(cache: repetitions == 1);
    Console.WriteLine($"Calibrating {judgeModel} against {cases.Count} labelled cases\n");

    Progress<string> progress = new(line => Console.WriteLine($"  {line}"));

    CalibrationReport report = await new CalibrationRunner(
            new TriadEvaluator(judgeClient, config.Triad), judgeModel)
        .RunAsync(cases, config.Triad, repetitions, progress);

    string path = Save($"calibration-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json", report);

    Console.WriteLine();
    Console.WriteLine("| Metric | n | exact | within 1 | MAE | bias | corr | band |");
    Console.WriteLine("| --- | --- | --- | --- | --- | --- | --- | --- |");

    foreach (MetricAgreement agreement in report.Agreement)
    {
        Console.WriteLine(
            $"| {agreement.Metric} | {agreement.Compared} | {agreement.ExactAgreement:P0} " +
            $"| {agreement.WithinOne:P0} | {agreement.MeanAbsoluteError:F2} | {agreement.Bias:+0.00;-0.00;0.00} " +
            $"| {agreement.Correlation:F2} | {agreement.BandAgreement:P0} |");
    }

    foreach (ConsistencySummary summary in report.Consistency)
    {
        Console.WriteLine(
            $"consistency {summary.Metric}: mean sd {summary.MeanStandardDeviation:F2}, " +
            $"worst range {summary.WorstRange:F1}, verdict flip rate {summary.VerdictFlipRate:P0}");

        foreach (CaseConsistency flipped in summary.Cases.Where(c => c.VerdictFlipped))
        {
            Console.WriteLine($"    FLIPS {flipped.CaseId}: {string.Join(", ", flipped.Scores)}");
        }
    }

    string[] blocking = report.Agreement
        .SelectMany(agreement => agreement.Disagreements.Select(d => $"{agreement.Metric}: {d}"))
        .ToArray();

    if (blocking.Length > 0)
    {
        Console.WriteLine();
        Console.WriteLine("## Gate-changing disagreements");
        Console.WriteLine("One side would block the merge and the other would not:");

        foreach (string disagreement in blocking)
        {
            Console.WriteLine($"- {disagreement}");
        }
    }

    Console.WriteLine();
    Console.WriteLine($"artifact: {path}");

    // Calibration is a measurement, not a gate: it tells you whether to trust the thresholds.
    return 0;
}

static (AIAgent Agent, IRunTelemetrySource Telemetry, string Model, UsageTracker Usage) BuildAgent(int repetitions = 1)
{
    // Repeated runs must bypass the cache, or every repetition replays one stored answer and the
    // reliability figures describe the cache rather than the agent.
    (IChatClient client, string model, UsageTracker usage) = ModelFactory.CreateCandidate(
        EvalPolicy.ShouldCacheCandidate(repetitions));
    KeywordRetriever retriever = KeywordRetriever.FromDirectory(RepoPaths.Corpus);
    (AIAgent agent, GuardrailRecorder recorder) = SupportAgentFactory.Create(client, retriever);

    return (agent, new RecorderTelemetrySource(recorder), model, usage);
}

static string Save<T>(string fileName, T payload)
{
    Directory.CreateDirectory(RepoPaths.RunsDirectory);
    string path = Path.Combine(RepoPaths.RunsDirectory, fileName);
    File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonDefaults.Options));

    return path;
}

static EvalConfig LoadConfig()
{
    using FileStream stream = File.OpenRead(RepoPaths.Config);

    return JsonSerializer.Deserialize<EvalConfig>(stream, JsonDefaults.Options)
        ?? throw new InvalidOperationException("eval-config.json is not valid.");
}

static int Help()
{
    Console.WriteLine("""
        evalrunner <command>

          rules                          Rule engine over frozen responses (offline, no credentials)
          tier2 [--repetitions N]        Pull-request gate: rules, retrieval, and the RAG triad
                [--no-triad]             Skip judge calls and gate on deterministic checks only
          safety [--repetitions N]       Adversarial suite: injection, jailbreak, extraction
          tier3 [--repetitions N]        Scheduled reliability run with confidence intervals
          tier3 --incident PATH [--judge] Replay a captured production trace against today's rules
          report [--run PATH]            Print the report for a saved run artifact
          calibrate [--repeat N] [--case ID] Score the judge against the human-labelled set;
                                         --repeat measures judge self-consistency

        Exit codes: 0 pass, 1 gate failure, 2 configuration error.
        """);

    return 0;
}













