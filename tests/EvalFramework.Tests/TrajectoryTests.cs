using EvalFramework.Datasets;
using EvalFramework.Execution;
using EvalFramework.Rules;
using EvalFramework.Trajectory;
using Microsoft.Extensions.AI;

namespace EvalFramework.Tests;

/// <summary>
/// Trajectory capture and aggregation.
/// </summary>
/// <remarks>
/// The final answer cannot show whether an agent reasoned soundly: guessing correctly, calling a
/// tool it did not need, or ignoring what a tool returned all produce plausible text. Judging the
/// path requires recording the path, and recording it badly would quietly change what is judged.
/// </remarks>
public sealed class TrajectoryTests
{
    private static ChatMessage Assistant(params AIContent[] contents) => new(ChatRole.Assistant, contents);

    private static readonly Dictionary<string, object?> LookupArgs = new() { ["orderId"] = "A-1" };

    private static ChatMessage[] ToolConversation() =>
    [
        Assistant(new FunctionCallContent("call-1", "lookup_order", LookupArgs)),
        new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "Order A-1: dispatched.")]),
        Assistant(new TextContent("Your order A-1 is dispatched."))
    ];

    [Fact]
    public void ToolCallsAndTheirResultsAreCapturedTogether()
    {
        IReadOnlyList<TrajectoryMessage> trajectory = Execution.Trajectory.Capture(ToolConversation());

        TrajectoryToolCall call = trajectory.SelectMany(turn => turn.ToolCalls).Single();

        Assert.Equal("lookup_order", call.Name);
        Assert.Equal("A-1", call.Arguments["orderId"]);
        Assert.Equal("Order A-1: dispatched.", call.Result);
    }

    [Fact]
    public void TheFinalAnswerIsRetained()
    {
        IReadOnlyList<TrajectoryMessage> trajectory = Execution.Trajectory.Capture(ToolConversation());

        Assert.Contains(trajectory, turn => turn.Text.Contains("dispatched", StringComparison.Ordinal));
    }

    [Fact]
    public void MessagesCarryingOnlyAToolResultAreNotKeptTwice()
    {
        // The result is already attached to the call that produced it.
        IReadOnlyList<TrajectoryMessage> trajectory = Execution.Trajectory.Capture(ToolConversation());

        Assert.Equal(2, trajectory.Count);
    }

    [Fact]
    public void ASavedTrajectoryCanBeRebuiltForJudgingWithoutRerunningTheAgent()
    {
        IReadOnlyList<TrajectoryMessage> trajectory = Execution.Trajectory.Capture(ToolConversation());

        IReadOnlyList<ChatMessage> rebuilt =
            Execution.Trajectory.ToChatMessages("Where is order A-1?", trajectory);

        Assert.Equal(ChatRole.User, rebuilt[0].Role);
        Assert.Contains(rebuilt, message => message.Contents.OfType<FunctionCallContent>().Any());
        Assert.Contains(rebuilt, message => message.Contents.OfType<FunctionResultContent>().Any());
        Assert.Contains("dispatched", rebuilt[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void RebuildingDoesNotDuplicateTheOpeningQuestion()
    {
        TrajectoryMessage[] trajectory =
        [
            new() { Role = "user", Text = "Where is order A-1?" },
            new() { Role = "assistant", Text = "It is dispatched." }
        ];

        IReadOnlyList<ChatMessage> rebuilt =
            Execution.Trajectory.ToChatMessages("Where is order A-1?", trajectory);

        Assert.Single(rebuilt, message => message.Role == ChatRole.User);
    }

    [Fact]
    public void ATrajectoryWithoutToolsStillRebuilds()
    {
        IReadOnlyList<TrajectoryMessage> trajectory = Execution.Trajectory.Capture(
            [Assistant(new TextContent("Open Account, then Subscriptions."))]);

        IReadOnlyList<ChatMessage> rebuilt =
            Execution.Trajectory.ToChatMessages("How do I cancel?", trajectory);

        Assert.Equal(2, rebuilt.Count);
    }

    private static TrajectoryResult Result(string caseId, params (string Metric, double? Score)[] scores) =>
        new(caseId, 1, scores.Select(s => new TrajectoryScore(s.Metric, s.Score, null)).ToArray());

    [Fact]
    public void ScoresAreReportedAsADistributionRatherThanAVerdict()
    {
        IReadOnlyList<TrajectoryMetricSummary> summary = TrajectorySummary.Summarize(
        [
            Result("a", (TrajectoryMetrics.TaskAdherence, 5)),
            Result("b", (TrajectoryMetrics.TaskAdherence, 3)),
            Result("c", (TrajectoryMetrics.TaskAdherence, 4))
        ]);

        TrajectoryMetricSummary adherence = summary.Single();

        Assert.Equal(3, adherence.Judged);
        Assert.Equal(4.0, adherence.Mean, 6);
        Assert.Equal(3.0, adherence.Min, 6);
        Assert.True(adherence.StandardDeviation > 0);
    }

    [Fact]
    public void WeakCasesAreNamedSoAHumanKnowsWhatToRead()
    {
        IReadOnlyList<TrajectoryMetricSummary> summary = TrajectorySummary.Summarize(
        [
            Result("healthy", (TrajectoryMetrics.IntentResolution, 5)),
            Result("struggling", (TrajectoryMetrics.IntentResolution, 2))
        ]);

        Assert.Equal(["struggling"], summary.Single().WorstCases);
    }

    [Fact]
    public void UnscoredMetricsAreExcludedRatherThanCountedAsZero()
    {
        // A judge failure must not drag the mean down and look like a quality regression.
        IReadOnlyList<TrajectoryMetricSummary> summary = TrajectorySummary.Summarize(
        [
            Result("a", (TrajectoryMetrics.TaskAdherence, 5)),
            Result("b", (TrajectoryMetrics.TaskAdherence, null))
        ]);

        Assert.Equal(1, summary.Single().Judged);
        Assert.Equal(5.0, summary.Single().Mean, 6);
    }
    [Fact]
    public void ScalesAreLabelledBecauseTheMetricsDoNotShareOne()
    {
        // Tool call accuracy is a boolean pass rate; the others are 1 to 5 ratings. Reading 0.75
        // as a poor rating rather than three calls in four would be an expensive misreading.
        IReadOnlyList<TrajectoryMetricSummary> summary = TrajectorySummary.Summarize(
        [
            Result("a", (TrajectoryMetrics.ToolCallAccuracy, 1), (TrajectoryMetrics.TaskAdherence, 4)),
            Result("b", (TrajectoryMetrics.ToolCallAccuracy, 0), (TrajectoryMetrics.TaskAdherence, 5))
        ]);

        TrajectoryMetricSummary tools = summary.Single(s => s.Metric == TrajectoryMetrics.ToolCallAccuracy);
        TrajectoryMetricSummary adherence = summary.Single(s => s.Metric == TrajectoryMetrics.TaskAdherence);

        Assert.Equal("0-1 pass rate", tools.Scale);
        Assert.Equal("1-5 rating", adherence.Scale);
        Assert.Equal(0.5, tools.Mean, 6);
    }

    [Fact]
    public void AFailedToolCallIsAWeakCaseButAThreeOutOfFiveRatingIsNot()
    {
        IReadOnlyList<TrajectoryMetricSummary> summary = TrajectorySummary.Summarize(
        [
            Result("wrong-tool", (TrajectoryMetrics.ToolCallAccuracy, 0)),
            Result("good-tool", (TrajectoryMetrics.ToolCallAccuracy, 1))
        ]);

        Assert.Equal(["wrong-tool"], summary.Single().WorstCases);
    }


    [Fact]
    public void NoJudgedTrajectoriesProducesNoSummaryRatherThanZeros()
    {
        Assert.Empty(TrajectorySummary.Summarize([]));
    }

    [Fact]
    public void TrajectorySurvivesTheArtifactRoundTrip()
    {
        GoldenCase goldenCase = new() { Id = "case", Query = "Where is order A-1?" };

        ResponseRecord record = new()
        {
            CaseId = goldenCase.Id,
            Query = goldenCase.Query,
            Repetition = 1,
            Response = "Your order A-1 is dispatched.",
            LatencyMs = 5,
            Rules = new RuleReport([]),
            Trajectory = Execution.Trajectory.Capture(ToolConversation())
        };

        RunArtifact original = AgentRunner.Build([goldenCase], [record], 1, "tier3", "m", "d.jsonl");
        string json = System.Text.Json.JsonSerializer.Serialize(original, JsonDefaults.Options);
        RunArtifact copy = System.Text.Json.JsonSerializer.Deserialize<RunArtifact>(json, JsonDefaults.Options)!;

        TrajectoryToolCall call = copy.Responses[0].Trajectory.SelectMany(t => t.ToolCalls).Single();

        Assert.Equal("lookup_order", call.Name);
        Assert.Equal("Order A-1: dispatched.", call.Result);

        // Arguments come back as JsonElement; rebuilding must still yield usable values.
        IReadOnlyList<ChatMessage> rebuilt =
            Execution.Trajectory.ToChatMessages(copy.Responses[0].Query, copy.Responses[0].Trajectory);

        FunctionCallContent rebuiltCall = rebuilt
            .SelectMany(message => message.Contents.OfType<FunctionCallContent>())
            .Single();

        Assert.Equal("A-1", rebuiltCall.Arguments!["orderId"]!.ToString());
    }
}



