using EvalFramework.Execution;
using EvalFramework.Rules;
using Microsoft.Extensions.AI;
using SupportAgent;
using SupportAgent.Guardrails;

namespace EvalFramework.Tests;

/// <summary>
/// Telemetry is captured per invocation and reset between them, which makes shared mutable state
/// dangerous: a later reset can erase evidence already recorded against an earlier case.
/// </summary>
public sealed class GuardrailRecorderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ToolArgumentRule Rule = new()
    {
        ToolName = "issue_refund",
        NumericRanges = new Dictionary<string, NumericRange> { ["amount"] = new(0, 500) }
    };

    private static FunctionInvocationContext Context(string tool, Dictionary<string, object?> arguments) => new()
    {
        Function = AIFunctionFactory.Create(() => "ok", tool),
        Arguments = new AIFunctionArguments(arguments),
        CallContent = new FunctionCallContent("id", tool, arguments)
    };

    [Fact]
    public async Task CapturedToolCallsSurviveALaterReset()
    {
        // The bug this pins: a live list handed to the runner was cleared before the next case,
        // so a tool that genuinely ran looked as though it had never been called.
        GuardrailRecorder recorder = new();
        ToolGuard guard = new([Rule], outcome => recorder.RecordToolCall(
            new ToolCallRecord(outcome.ToolName, outcome.Arguments, outcome.Rejected)));

        await guard.InvokeAsync(
            agent: null!,
            Context("issue_refund", new() { ["amount"] = 100 }),
            (_, _) => ValueTask.FromResult<object?>("done"),
            Ct);

        IReadOnlyList<ToolCallRecord> captured = recorder.ToolCalls;
        recorder.Reset();

        Assert.Single(captured);
        Assert.Equal("issue_refund", captured[0].Name);
        Assert.Empty(recorder.ToolCalls);
    }

    [Fact]
    public async Task AllowedAndRejectedCallsAreBothRecorded()
    {
        GuardrailRecorder recorder = new();
        ToolGuard guard = new([Rule], outcome => recorder.RecordToolCall(
            new ToolCallRecord(outcome.ToolName, outcome.Arguments, outcome.Rejected)));

        await guard.InvokeAsync(
            agent: null!,
            Context("issue_refund", new() { ["amount"] = 100 }),
            (_, _) => ValueTask.FromResult<object?>("done"),
            Ct);

        await guard.InvokeAsync(
            agent: null!,
            Context("issue_refund", new() { ["amount"] = 5000 }),
            (_, _) => ValueTask.FromResult<object?>("done"),
            Ct);

        Assert.Equal(2, recorder.ToolCalls.Count);
        Assert.False(recorder.ToolCalls[0].Rejected);
        Assert.True(recorder.ToolCalls[1].Rejected);
    }

    [Fact]
    public async Task ArgumentsAreCopiedSoLaterMutationCannotRewriteHistory()
    {
        GuardrailRecorder recorder = new();
        ToolGuard guard = new([Rule], outcome => recorder.RecordToolCall(
            new ToolCallRecord(outcome.ToolName, outcome.Arguments, outcome.Rejected)));

        Dictionary<string, object?> arguments = new() { ["amount"] = 100 };
        FunctionInvocationContext context = Context("issue_refund", arguments);

        await guard.InvokeAsync(agent: null!, context, (_, _) => ValueTask.FromResult<object?>("done"), Ct);

        context.Arguments["amount"] = 9999;

        Assert.Equal("100", recorder.ToolCalls[0].Arguments["amount"]!.ToString());
    }

    [Fact]
    public void ResetClearsEverythingFromThePreviousRequest()
    {
        GuardrailRecorder recorder = new();
        recorder.RecordToolCall(new ToolCallRecord("t", new Dictionary<string, object?>()));

        recorder.Reset();

        Assert.Empty(recorder.ToolCalls);
        Assert.Null(recorder.LastRetrieval);
        Assert.Null(recorder.LastResponse);
    }
}
