using System.Text.Json;
using EvalFramework;
using EvalFramework.Datasets;
using EvalFramework.Deterministic;
using EvalFramework.Execution;
using EvalFramework.Judging;
using EvalFramework.Reporting;
using EvalFramework.Statistics;
using EvalRunner;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

CommandLine cli = new(args);

try
{
    return cli.Command switch
    {
        "tier1" => Tier1(),
        "tier2" => await Tier2Async(cli),
        "tier3" => await Tier3Async(cli),
        "report" => Report(cli),
        _ => Help()
    };
}
catch (InvalidOperationException error)
{
    Console.Error.WriteLine($"error: {error.Message}");
    return 2;
}

// Tier 1: deterministic rules over frozen responses. No credentials, no network, no flake.
static int Tier1()
{
    IReadOnlyList<GoldenCase> cases = GoldenSet.Load(RepoPaths.GoldenSet);
    RecordedResponseSet recorded = RecordedResponseSet.Load(RepoPaths.RecordedResponses);

    Dictionary<string, string> lookup = recorded.Responses
        .ToDictionary(item => item.CaseId, item => item.Response, StringComparer.OrdinalIgnoreCase);

    int failures = 0;
    Console.WriteLine($"Tier 1 deterministic checks over {cases.Count} cases\n");

    foreach (GoldenCase goldenCase in cases)
    {
        if (!lookup.TryGetValue(goldenCase.Id, out string? response))
        {
            Console.WriteLine($"[FAIL] {goldenCase.Id}: no recorded response");
            failures++;
            continue;
        }

        DeterministicResult result = DeterministicEvaluator.Evaluate(goldenCase, response);
        Console.WriteLine($"[{(result.Passed ? "PASS" : "FAIL")}] {goldenCase.Id}");

        foreach (CheckResult check in result.Checks.Where(check => !check.Passed))
        {
            Console.WriteLine($"        {check.Name}: {check.Detail}");
        }

        if (!result.Passed)
        {
            failures++;
        }
    }

    Console.WriteLine($"\n{cases.Count - failures}/{cases.Count} cases passed");
    return failures == 0 ? 0 : 1;
}

// Tier 2: reliability of the real agent, measured over repeated runs.
static async Task<int> Tier2Async(CommandLine cli)
{
    IReadOnlyList<GoldenCase> cases = GoldenSet.Load(RepoPaths.GoldenSet);
    EvalConfig config = LoadConfig(cli.IntOption("--repetitions"));

    (IChatClient client, string model) = ModelFactory.CreateCandidate();
    AIAgent agent = new ChatClientAgent(
        client,
        instructions: """
            You are a customer support agent. Answer concisely and politely.
            Give numbered steps, state exactly what information support will need,
            and never give medical, legal, or financial advice.
            """,
        name: "SupportAgent");

    Console.WriteLine($"Tier 2: {cases.Count} cases x {config.Repetitions} repetitions on {model}\n");

    Progress<string> progress = new(line => Console.WriteLine($"  {line}"));
    RunArtifact run = await new GoldenSetRunner(agent, model)
        .RunAsync(cases, config, RepoPaths.GoldenSet, progress);

    Directory.CreateDirectory(RepoPaths.RunsDirectory);
    string path = Path.Combine(RepoPaths.RunsDirectory, $"tier2-{run.RunId}.json");
    File.WriteAllText(path, JsonSerializer.Serialize(run, JsonDefaults.Options));

    GateReport gates = RunAnalyzer.ApplyGates(run, config);
    Console.WriteLine();
    Console.WriteLine(MarkdownReport.ForRun(run, gates));
    Console.WriteLine($"artifact: {path}");

    return gates.Passed ? 0 : 1;
}

// Tier 3: judge the responses Tier 2 already paid for.
static async Task<int> Tier3Async(CommandLine cli)
{
    string runPath = cli.Option("--run")
        ?? RepoPaths.LatestRun()
        ?? throw new InvalidOperationException("No Tier 2 artifact found. Run tier2 first.");

    RunArtifact run = LoadRun(runPath);
    (IChatClient client, string model) = ModelFactory.CreateJudge();

    Console.WriteLine($"Tier 3: judging run {run.RunId} with {model}\n");

    Progress<string> progress = new(line => Console.WriteLine($"  {line}"));
    JudgeArtifact artifact = await new JudgeRunner(client, model, "support-quality-v1")
        .JudgeAsync(run, cli.IntOption("--sample-per-case") ?? 1, progress);

    string path = Path.Combine(RepoPaths.RunsDirectory, $"tier3-{run.RunId}.json");
    File.WriteAllText(path, JsonSerializer.Serialize(artifact, JsonDefaults.Options));

    Console.WriteLine();
    Console.WriteLine(MarkdownReport.ForJudge(artifact));
    Console.WriteLine($"artifact: {path}");

    IReadOnlyList<string> violations = JudgeSummarizer.ApplyThresholds(
        artifact.Summary,
        cli.DoubleOption("--min-mean", 4.0),
        cli.DoubleOption("--min-score", 3.0));

    foreach (string violation in violations)
    {
        Console.WriteLine($"gate violation: {violation}");
    }

    return violations.Count == 0 ? 0 : 1;
}

static int Report(CommandLine cli)
{
    string runPath = cli.Option("--run")
        ?? RepoPaths.LatestRun()
        ?? throw new InvalidOperationException("No Tier 2 artifact found.");

    RunArtifact run = LoadRun(runPath);
    Console.WriteLine(MarkdownReport.ForRun(run, RunAnalyzer.ApplyGates(run, LoadConfig(null))));
    return 0;
}

static RunArtifact LoadRun(string path)
{
    using FileStream stream = File.OpenRead(path);
    return JsonSerializer.Deserialize<RunArtifact>(stream, JsonDefaults.Options)
        ?? throw new InvalidOperationException($"{path} is not a valid Tier 2 artifact.");
}

static EvalConfig LoadConfig(int? repetitionOverride)
{
    using FileStream stream = File.OpenRead(RepoPaths.Config);
    EvalConfig config = JsonSerializer.Deserialize<EvalConfig>(stream, JsonDefaults.Options)
        ?? throw new InvalidOperationException("eval-config.json is not valid.");

    return repetitionOverride is int repetitions
        ? config with { Repetitions = repetitions }
        : config;
}

static int Help()
{
    Console.WriteLine("""
        evalrunner <command>

          tier1                          Deterministic checks over frozen responses (offline)
          tier2 [--repetitions N]        Run the agent over the golden set and apply statistical gates
          tier3 [--run PATH]             Judge a Tier 2 artifact with a model
                [--sample-per-case N] [--min-mean X] [--min-score X]
          report [--run PATH]            Print the Markdown report for a Tier 2 artifact

        Exit codes: 0 pass, 1 gate failure, 2 configuration error.
        """);

    return 0;
}
