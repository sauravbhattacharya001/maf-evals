using System.Text.Json;
using EvalFramework;
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
        _ => Help()
    };
}
catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
{
    // A broken artifact or missing configuration is a diagnosable condition, not a stack trace.
    Console.Error.WriteLine($"error: {error.Message}");
    return 2;
}

// Offline check of the rule engine itself against frozen responses. Tier 1 proper runs
// inside the agent at request time; this proves the rules it depends on still behave.
static int Rules()
{
    IReadOnlyList<GoldenCase> cases = GoldenSet.Load(RepoPaths.GoldenSet);
    RecordedResponseSet recorded = RecordedResponseSet.Load(RepoPaths.RecordedResponses);

    Dictionary<string, string> lookup = recorded.Responses
        .ToDictionary(item => item.CaseId, item => item.Response, StringComparer.OrdinalIgnoreCase);

    int failures = 0;
    Console.WriteLine($"Rule checks over {cases.Count} frozen responses\n");

    foreach (GoldenCase goldenCase in cases)
    {
        if (!lookup.TryGetValue(goldenCase.Id, out string? response))
        {
            Console.WriteLine($"[FAIL] {goldenCase.Id}: no recorded response");
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

// Tier 2: the pull-request gate. One pass per case, rules plus the RAG triad.
static async Task<int> Tier2Async(CommandLine cli)
{
    IReadOnlyList<GoldenCase> cases = GoldenSet.Load(RepoPaths.GoldenSet);
    EvalConfig config = LoadConfig();
    int repetitions = cli.IntOption("--repetitions") ?? config.Tier2Repetitions;

    (AIAgent agent, IRunTelemetrySource telemetry, string model) = BuildAgent();

    Console.WriteLine($"Tier 2: {cases.Count} cases x {repetitions} on {model}\n");
    Progress<string> progress = new(line => Console.WriteLine($"  {line}"));

    RunArtifact run = await new AgentRunner(agent, model, telemetry)
        .RunAsync(cases, repetitions, tier: "tier2", RepoPaths.GoldenSet, progress);

    List<TriadResult> triad = [];
    bool triadEvaluated = !cli.HasFlag("--no-triad");

    if (triadEvaluated)
    {
        (IChatClient judgeClient, string judgeModel) = ModelFactory.CreateJudge();
        Console.WriteLine($"\nJudging with {judgeModel}");

        TriadEvaluator evaluator = new(judgeClient, config.Triad);

        foreach (ResponseRecord record in run.Responses.Where(record => !record.Blocked))
        {
            triad.Add(await evaluator.EvaluateAsync(record, progress));
        }
    }

    Tier2Result result = Tier2Gate.Apply(run, cases, triad, config.Triad, triadEvaluated);
    string path = Save($"tier2-{run.RunId}.json", result);

    Console.WriteLine();
    Console.WriteLine(MarkdownReport.ForTier2(result));
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

    (AIAgent agent, IRunTelemetrySource telemetry, string model) = BuildAgent();

    Console.WriteLine($"Tier 3: {cases.Count} cases x {repetitions} on {model}\n");
    Progress<string> progress = new(line => Console.WriteLine($"  {line}"));

    RunArtifact run = await new AgentRunner(agent, model, telemetry)
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
        (IChatClient judgeClient, string judgeModel) = ModelFactory.CreateJudge();
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

static (AIAgent Agent, IRunTelemetrySource Telemetry, string Model) BuildAgent()
{
    (IChatClient client, string model) = ModelFactory.CreateCandidate();
    KeywordRetriever retriever = KeywordRetriever.FromDirectory(RepoPaths.Corpus);
    (AIAgent agent, GuardrailRecorder recorder) = SupportAgentFactory.Create(client, retriever);

    return (agent, new RecorderTelemetrySource(recorder), model);
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
          tier3 [--repetitions N]        Scheduled reliability run with confidence intervals
          tier3 --incident PATH [--judge] Replay a captured production trace against today's rules
          report [--run PATH]            Print the report for a saved run artifact

        Exit codes: 0 pass, 1 gate failure, 2 configuration error.
        """);

    return 0;
}



