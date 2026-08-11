using System.Text.Json;
using EvalFramework.Datasets;
using EvalFramework.Execution;
using EvalFramework.Statistics;

namespace EvalFramework.Tests;

/// <summary>
/// Tool choice is recorded in the trace, so it is a fact to compare rather than a judgement to
/// score. Checking it deterministically is both cheaper and more reliable than asking a model.
/// </summary>
public sealed class ToolCallCheckTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static GoldenCase Case(params ExpectedToolCall[] expected) => new()
    {
        Id = "case",
        Query = "refund order A-1 for 120",
        ExpectedToolCalls = expected
    };

    private static ExpectedToolCall Expect(string name, string? argumentsJson = null) => new()
    {
        Name = name,
        Arguments = argumentsJson is null
            ? new Dictionary<string, JsonElement>()
            : JsonDocument.Parse(argumentsJson).RootElement.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.Clone())
    };

    private static ToolCallRecord Call(string name, Dictionary<string, object?>? args = null, bool rejected = false) =>
        new(name, args ?? [], rejected);

    [Fact]
    public void ACaseWithNoExpectationsAlwaysPasses()
    {
        Assert.True(ToolCallCheck.Evaluate(Case(), [Call("anything")]).Passed);
    }

    [Fact]
    public void CallingTheExpectedToolPasses()
    {
        ToolCallCheck.Result result = ToolCallCheck.Evaluate(
            Case(Expect("issue_refund")), [Call("issue_refund")]);

        Assert.True(result.Passed);
    }

    [Fact]
    public void NotCallingTheToolAtAllIsReportedWithWhatWasCalledInstead()
    {
        ToolCallCheck.Result result = ToolCallCheck.Evaluate(
            Case(Expect("issue_refund")), [Call("lookup_order")]);

        Assert.False(result.Passed);
        Assert.Contains("called instead: lookup_order", result.Problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void CallingNoToolsIsDistinguishedFromCallingTheWrongOne()
    {
        ToolCallCheck.Result result = ToolCallCheck.Evaluate(Case(Expect("issue_refund")), []);

        Assert.Contains("no tools were called", result.Problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ARejectedCallDoesNotSatisfyAnExpectation()
    {
        // The guard stopped it, so the action never happened.
        ToolCallCheck.Result result = ToolCallCheck.Evaluate(
            Case(Expect("issue_refund")), [Call("issue_refund", rejected: true)]);

        Assert.False(result.Passed);
        Assert.Contains("rejected by a guard", result.Problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ArgumentsAreMatchedAsASubset()
    {
        // Extra arguments are harmless; the listed ones must match.
        ToolCallCheck.Result result = ToolCallCheck.Evaluate(
            Case(Expect("issue_refund", """{"orderId":"A-1"}""")),
            [Call("issue_refund", new() { ["orderId"] = "A-1", ["amount"] = 120, ["reason"] = "damaged" })]);

        Assert.True(result.Passed);
    }

    [Fact]
    public void AWrongArgumentValueIsCaught()
    {
        ToolCallCheck.Result result = ToolCallCheck.Evaluate(
            Case(Expect("issue_refund", """{"orderId":"A-1"}""")),
            [Call("issue_refund", new() { ["orderId"] = "A-999" })]);

        Assert.False(result.Passed);
        Assert.Contains("argument mismatch", result.Problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void NumbersMatchAcrossJsonAndClrRepresentations()
    {
        // Arguments arrive as JsonElement from the model and as boxed numbers in tests.
        ToolCallCheck.Result result = ToolCallCheck.Evaluate(
            Case(Expect("issue_refund", """{"amount":120}""")),
            [Call("issue_refund", new() { ["amount"] = 120.0 })]);

        Assert.True(result.Passed);
    }

    [Fact]
    public void JsonElementArgumentsFromARoundTrippedArtifactStillMatch()
    {
        Dictionary<string, object?> arguments = JsonDocument
            .Parse("""{"orderId":"A-1","amount":120}""")
            .RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => (object?)p.Value.Clone());

        ToolCallCheck.Result result = ToolCallCheck.Evaluate(
            Case(Expect("issue_refund", """{"orderId":"A-1","amount":120}""")),
            [Call("issue_refund", arguments)]);

        Assert.True(result.Passed);
    }

    [Fact]
    public void OneMatchingCallAmongSeveralIsEnough()
    {
        ToolCallCheck.Result result = ToolCallCheck.Evaluate(
            Case(Expect("issue_refund", """{"orderId":"A-1"}""")),
            [Call("issue_refund", new() { ["orderId"] = "A-9" }),
             Call("issue_refund", new() { ["orderId"] = "A-1" })]);

        Assert.True(result.Passed);
    }
    [Fact]
    public void AForbiddenToolThatSucceededIsAViolation()
    {
        GoldenCase forbids = new() { Id = "c", Query = "q", ForbiddenToolCalls = ["issue_refund"] };

        ToolCallCheck.Result result = ToolCallCheck.Evaluate(forbids, [Call("issue_refund")]);

        Assert.False(result.Passed);
        Assert.Contains("forbids", result.Problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AForbiddenToolStoppedByAGuardIsNotAViolation()
    {
        // Attempting an over-limit refund and being blocked is acceptable behaviour; completing
        // one is not. Collapsing the two would make the guard invisible to the gate.
        GoldenCase forbids = new() { Id = "c", Query = "q", ForbiddenToolCalls = ["issue_refund"] };

        ToolCallCheck.Result result = ToolCallCheck.Evaluate(
            forbids, [Call("issue_refund", rejected: true)]);

        Assert.True(result.Passed);
    }

    [Fact]
    public void NotCallingAForbiddenToolPasses()
    {
        GoldenCase forbids = new() { Id = "c", Query = "q", ForbiddenToolCalls = ["issue_refund"] };

        Assert.True(ToolCallCheck.Evaluate(forbids, []).Passed);
    }


    [Fact]
    public void EveryUnmetExpectationIsReported()
    {
        ToolCallCheck.Result result = ToolCallCheck.Evaluate(
            Case(Expect("lookup_order"), Expect("issue_refund")), []);

        Assert.Equal(2, result.Problems.Count);
    }
}

