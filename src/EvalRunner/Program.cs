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
using EvalFramework.Trajectory;
using EvalRunner;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SupportAgent;
using SupportAgent.Retrieval;

CommandLine cli = new(args);
DotEnv.Load();

try
{
    cli.Validate();

    return cli.Command switch
    {
        "rules" => Rules(),
        "tier2" => await Tier2Async(cli),
        "tier3" => await Tier3Async(cli),
        "incident" => await ReplayIncidentAsync(cli),
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

    // A case may have several known-good answers, including ones captured from real runs.
    ILookup<string, string> lookup = positives.Fixtures
        .ToLookup(item => item.CaseId, item => item.Response, StringComparer.OrdinalIgnoreCase);

    int failures = 0;
    Console.WriteLine($"Rule checks over {cases.Count} known-good responses\n");

    foreach (GoldenCase goldenCase in cases)
    {
        string[] responses = lookup[goldenCase.Id].ToArray();

        if (responses.Length == 0)
        {
            Console.WriteLine($"[FAIL] {goldenCase.Id}: no positive fixture");
            failures++;
            continue;
        }

        bool caseFailed = false;

        foreach (string response in responses)
        {
            RuleReport report = ResponseRules.Evaluate(goldenCase.ToRuleSet(), response);

            foreach (CheckResult check in report.Failures)
            {
                Console.WriteLine($"        {check.Severity.ToString().ToUpperInvariant()} {check.Name}: {check.Detail}");
            }

            caseFailed |= !report.Passed;
        }

        Console.WriteLine($"[{(caseFailed ? "FAIL" : "PASS")}] {goldenCase.Id} ({responses.Length} fixture(s))");

        if (caseFailed)
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

    // Semantic expectations run here, never in Tier 1: the hot path must stay free of network calls.
    if (cases.Any(item => item.SemanticExpectations.Count > 0))
    {
        (IEmbeddingGenerator<string, Embedding<float>> embedder, string embedModel) =
            ModelFactory.CreateEmbedder();

        Console.WriteLine($"\nChecking semantic expectations with {embedModel}");
        SemanticRuleEvaluator semantic = new(embedder);

        Dictionary<string, GoldenCase> byId = cases.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        List<ResponseRecord> updated = [];

        foreach (ResponseRecord record in run.Responses)
        {
            if (!byId.TryGetValue(record.CaseId, out GoldenCase? goldenCase)
                || goldenCase.SemanticExpectations.Count == 0
                || !record.Counts)
            {
                updated.Add(record);
                continue;
            }

            RuleReport semanticReport =
                await semantic.EvaluateAsync(goldenCase.SemanticExpectations, record.Response);

            updated.Add(record with
            {
                Rules = new RuleReport([.. record.Rules.Checks, .. semanticReport.Checks])
            });
        }

        run = run with { Responses = updated, Cases = RunAnalyzer.Summarize(cases, updated) };
    }

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
// Tier 3: model as judge over the reasoning trajectory. Nothing else lives here.
// It answers a question the other tiers cannot: not whether the answer was acceptable, but whether
// the agent reasoned its way there. Reported as a distribution and never gating, because a judge
// measured flipping verdicts on identical input cannot decide a merge.
static async Task<int> Tier3Async(CommandLine cli)
{
    IReadOnlyList<GoldenCase> cases = GoldenSet.Load(RepoPaths.GoldenSet);
    EvalConfig config = LoadConfig();

    RunArtifact run;

    // Trajectories already recorded by an earlier run can be judged without paying for the agent
    // again, which is the same rule the triad follows: never buy the same response twice.
    if (cli.Option("--run") is string existing)
    {
        run = ArtifactReader.ReadRun(existing);
        Console.WriteLine($"Tier 3: judging trajectories from {Path.GetFileName(existing)}\n");
    }
    else
    {
        (AIAgent agent, IRunTelemetrySource telemetry, string model, UsageTracker usage) = BuildAgent();

        Console.WriteLine($"Tier 3: {cases.Count} cases on {model}\n");
        Progress<string> runProgress = new(line => Console.WriteLine($"  {line}"));

        run = await new AgentRunner(
                agent, model, telemetry, TimeSpan.FromSeconds(config.CallTimeoutSeconds), usage, config.Pricing)
            .RunAsync(cases, repetitions: 1, tier: "tier3", RepoPaths.GoldenSet, runProgress);
    }

    ResponseRecord[] judgeable = run.Responses
        .Where(record => record.Counts && !record.Blocked && record.Trajectory.Count > 0)
        .ToArray();

    if (judgeable.Length == 0)
    {
        Console.Error.WriteLine("error: no trajectories to judge.");
        return 2;
    }

    (IChatClient judgeClient, string judgeModel, UsageTracker judgeUsage) =
        ModelFactory.CreateJudge(cache: false);

    Console.WriteLine($"Judging {Plural(judgeable.Length)} with {judgeModel}\n");
    Progress<string> progress = new(line => Console.WriteLine($"  {line}"));

    IReadOnlyList<TrajectoryResult> judged = await new TrajectoryEvaluator(
            judgeClient, SupportPolicy.CreateTools(), TimeSpan.FromSeconds(config.CallTimeoutSeconds))
        .EvaluateManyAsync(judgeable, config.JudgeConcurrency, progress);

    run = run with
    {
        Tier = "tier3",
        TrajectoryResults = judged,
        TrajectorySummary = TrajectorySummary.Summarize(judged)
    };

    string path = Save($"tier3-{run.RunId}.json", run);

    Console.WriteLine();
    Console.WriteLine("| Trajectory metric | scale | judged | mean | sd | min | weak cases |");
    Console.WriteLine("| --- | --- | --- | --- | --- | --- | --- |");

    foreach (TrajectoryMetricSummary summary in run.TrajectorySummary)
    {
        string weak = summary.WorstCases.Count == 0 ? "-" : string.Join(", ", summary.WorstCases);
        Console.WriteLine(
            $"| {summary.Metric} | {summary.Scale} | {summary.Judged} | {summary.Mean:F2} " +
            $"| {summary.StandardDeviation:F2} | {summary.Min:F1} | {weak} |");
    }

    Console.WriteLine();
    Console.WriteLine($"judge spend: {judgeUsage.Snapshot(config.Pricing).EstimatedCostUsd:C4}");
    Console.WriteLine($"artifact: {path}");

    // A trend, not a verdict. Exit zero unless the run could not be produced at all.
    return 0;
}

static string Plural(int count) => count == 1 ? "1 trajectory" : $"{count} trajectories";

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

// Incident replay. Its own command rather than part of a tier: it is a diagnostic run against one
// captured trace, not a scheduled measurement of the agent. Offline unless a judge is asked for.
static async Task<int> ReplayIncidentAsync(CommandLine cli)
{
    string incidentPath = cli.Option("--trace")
        ?? throw new InvalidOperationException("Pass --trace PATH to replay a captured incident.");

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
    if (cli.HasFlag("--semantic"))
    {
        return await CalibrateSemanticAsync();
    }
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

// Chooses semantic thresholds from the labelled fixtures rather than by inspection.
static async Task<int> CalibrateSemanticAsync()
{
    IReadOnlyList<GoldenCase> cases = GoldenSet.Load(RepoPaths.GoldenSet);
    PositiveFixtureSet positives = PositiveFixtureSet.Load(RepoPaths.PositiveFixtures);
    NegativeFixtureSet negatives = NegativeFixtureSet.Load(RepoPaths.NegativeFixtures);

    (IEmbeddingGenerator<string, Embedding<float>> embedder, string model) = ModelFactory.CreateEmbedder();
    Console.WriteLine($"Measuring semantic expectations with {model}\n");

    IReadOnlyList<SemanticSeparation> results = await SemanticCalibration.MeasureAsync(
        cases, positives, negatives, new SemanticRuleEvaluator(embedder));

    if (results.Count == 0)
    {
        Console.WriteLine("No case declares a semantic expectation.");
        return 0;
    }

    Console.WriteLine("| Case | Expectation | Must pass | Must fail | Margin | Now | Suggested |");
    Console.WriteLine("| --- | --- | --- | --- | --- | --- | --- |");

    foreach (SemanticSeparation result in results)
    {
        string suggested = result.SuggestedThreshold is double value
            ? value.ToString("F2")
            : "none, references overlap";

        Console.WriteLine(
            $"| {result.CaseId} | {result.Expectation} | {result.MinAccepted:F2} ({result.Accepted}) " +
            $"| {result.MaxRejected:F2} ({result.Rejected}) | {result.Margin:+0.00;-0.00} " +
            $"| {result.CurrentThreshold:F2} | {suggested} |");
    }

    SemanticSeparation[] broken = results.Where(result => !result.CurrentThresholdWorks).ToArray();

    if (broken.Length > 0)
    {
        Console.WriteLine();

        foreach (SemanticSeparation result in broken)
        {
            Console.WriteLine(result.Separable
                ? $"  {result.CaseId}: the current threshold {result.CurrentThreshold:F2} is wrong. Use {result.SuggestedThreshold:F2}."
                : $"  {result.CaseId}: no threshold separates the examples. Improve the reference statements.");
        }
    }

    // A measurement, not a gate.
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
          tier3 [--run PATH]             Model as judge over the reasoning trajectory
          incident --trace PATH [--judge]  Replay one captured production trace
          report [--run PATH]            Print the report for a saved run artifact
          calibrate [--repeat N] [--case ID] Score the judge against the human-labelled set;
                                         --repeat measures judge self-consistency

        Exit codes: 0 pass, 1 gate failure, 2 configuration error.
        """);

    return 0;
}




















